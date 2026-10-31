// Derived from Graphify (https://github.com/Graphify-Labs/graphify), Apache-2.0.
// Changed for BrainX: rewritten in C# on tree-sitter, with every call site kept
// and full declaration ranges. See ThirdParty/Graphify/NOTICE.
using System.Text.RegularExpressions;
using TreeSitter;

namespace BrainX.CodeGraph;

/// <summary>
/// Tree-sitter declaration and reference pass for Graphify's other language
/// families. The AST supplies sites and lexical containers; NameResolver places
/// names conservatively and marks non-local matches INFERRED or AMBIGUOUS.
/// </summary>
public sealed partial class SyntaxFileExtractor : IFileExtractor
{
    private const int MaxNameLength = 160;

    private readonly string _grammar;
    public string Language { get; }
    public IReadOnlyCollection<string> Extensions { get; }
    public string Version => "tree-sitter/1+brainx-syntax/2";

    private SyntaxFileExtractor(string language, string grammar, params string[] extensions)
    { Language = language; _grammar = grammar; Extensions = extensions; }

    public static IReadOnlyList<IFileExtractor> Defaults { get; } =
    [
        new SyntaxFileExtractor("javascript", "JavaScript", ".js", ".mjs", ".cjs", ".jsx"),
        new SyntaxFileExtractor("typescript", "TypeScript", ".ts", ".mts", ".cts"),
        new SyntaxFileExtractor("tsx", "TSX", ".tsx"),
        new SyntaxFileExtractor("python", "Python", ".py", ".pyi"),
        new SyntaxFileExtractor("php", "PHP", ".php")
    ];

    public CodeFileUnit Extract(string relativePath, string text, IRepoFileSet repoFiles)
    {
        var unit = new CodeFileUnit { Path = relativePath, Language = Language, Extractor = Version };
        using var language = new Language(_grammar);
        using var parser = new Parser(language);
        using var tree = parser.Parse(text);
        if (tree == null) { unit.HasErrors = true; unit.Skipped = "parser returned no tree"; return unit; }
        var root = tree.RootNode;
        unit.LineCount = text.Count(c => c == '\n') + 1;
        unit.HasErrors = root.HasError;
        var keys = new HashSet<string>(StringComparer.Ordinal);

        // An explicit stack, not recursion: `a + b + …` thousands of terms long
        // is a tree that deep, and a stack overflow ends the whole MCP process
        // with nothing to catch. Children go on in reverse to keep source order.
        var stack = new Stack<(Node Node, UnitSymbol? Parent)>();
        stack.Push((root, null));
        while (stack.Count > 0)
        {
            var (node, parent) = stack.Pop();
            var current = parent;
            var (name, kind) = Declaration(node);
            if (!string.IsNullOrWhiteSpace(name))
            {
                var qualified = parent == null ? name : (parent.Qualified ?? parent.Name) + "." + name;
                var baseKey = relativePath + "#" + qualified;
                var key = baseKey;
                for (var i = 2; !keys.Add(key); i++) key = baseKey + "~" + i;
                current = new UnitSymbol { Key = key, Name = name, Qualified = parent == null ? null : qualified,
                    Kind = kind, Line = node.StartPosition.Row + 1, EndLine = Math.Max(node.StartPosition.Row + 1, node.EndPosition.Row + 1),
                    ParentKey = parent?.Key };
                unit.Symbols.Add(current);
            }
            if (CallKind(node.Type) is { } relation)
            {
                var target = CallTarget(node);
                var (called, receiver) = TargetName(target);
                if (!string.IsNullOrEmpty(called)) unit.Refs.Add(new UnitRef { FromKey = current?.Key,
                    Relation = relation, TargetName = called, Receiver = receiver, Line = target?.StartPosition.Row + 1 ?? node.StartPosition.Row + 1 });
            }
            Imports(node, relativePath, repoFiles, unit);
            if (node.Type is "class_definition" or "class_declaration" or "abstract_class_declaration" or "interface_declaration" or "trait_declaration")
                Bases(node, current, unit);
            var children = node.NamedChildren;
            for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], current));
        }
        return unit;
    }

    private (string? Name, string Kind) Declaration(Node node)
    {
        var type = node.Type;
        var kind = type switch
        {
            "class_definition" or "class_declaration" or "abstract_class_declaration" => "class",
            "interface_declaration" => "interface",
            "trait_declaration" => "trait",
            "enum_declaration" => "enum",
            "type_alias_declaration" => "type",
            "function_definition" or "function_declaration" or "generator_function_declaration" => "function",
            "method_definition" or "method_declaration" or "abstract_method_signature" => "method",
            _ => ""
        };
        if (kind.Length == 0 && type == "variable_declarator" && IsFunction(node.GetChildForField("value"))) kind = "function";
        if (kind.Length == 0 && type is "public_field_definition" or "field_definition" && IsFunction(node.GetChildForField("value"))) kind = "method";
        if (kind.Length == 0) return (null, "");
        var name = (node.GetChildForField("name") ?? node.GetChildForField("property"))?.Text;
        name = name?.Trim(' ', '\t', '\r', '\n', '$', '\'', '"');
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength || !Identifier().IsMatch(name)) return (null, "");
        return (name, kind);
    }

    private static bool IsFunction(Node? value) => value?.Type is "arrow_function" or "function_expression" or "function";

    private static string? CallKind(string type) => type switch
    {
        "call_expression" or "call" or "function_call_expression" or "member_call_expression"
            or "nullsafe_member_call_expression" or "scoped_call_expression" => "calls",
        "new_expression" or "object_creation_expression" => "creates",
        _ => null
    };

    // PHP's member and scoped calls ARE their own target. Their "name" field is
    // only the method, and taking it would lose what it was called on.
    private static Node? CallTarget(Node node) => node.Type is "member_call_expression" or "nullsafe_member_call_expression" or "scoped_call_expression"
        ? node
        : node.GetChildForField("function") ?? node.GetChildForField("constructor") ?? node.GetChildForField("class")
          ?? node.GetChildForField("name") ?? node.NamedChildren.FirstOrDefault();

    private static (string? Name, string? Receiver) TargetName(Node? target)
    {
        if (target == null) return (null, null);
        if (target.Type is "member_expression" or "attribute" or "member_call_expression" or "nullsafe_member_call_expression" or "scoped_call_expression")
        {
            var name = target.GetChildForField("property")?.Text ?? target.GetChildForField("attribute")?.Text
                ?? target.GetChildForField("name")?.Text ?? target.NamedChildren.LastOrDefault()?.Text;
            var receiver = target.GetChildForField("object") ?? target.GetChildForField("scope") ?? target.NamedChildren.FirstOrDefault();
            return (Simple(name), ReceiverOf(receiver));
        }
        return (Simple(target.Text), null);
    }

    /// <summary>
    /// What a call was made on, as a dotted path: <c>this</c>, <c>this.client</c>,
    /// <c>App.Foo</c>. Never null for a member call: anything that is not a plain
    /// path, <c>getClient().send()</c> say, is "?", which still says the call has
    /// a receiver. A null here would read as a bare <c>send()</c> and bind it to a
    /// top-level function of that name as if the syntax had shown it.
    /// </summary>
    private static string ReceiverOf(Node? node)
    {
        var text = node?.Text.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxNameLength) return "?";
        var path = text.Replace("?->", ".").Replace("->", ".").Replace("?.", ".").Replace("::", ".")
            .Replace('\\', '.').Replace("$", "", StringComparison.Ordinal).TrimStart('.');
        return DottedPath().IsMatch(path) ? path : "?";
    }

    private static string? Simple(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxNameLength) return null;
        var name = text.Trim().TrimStart('$');
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        return Identifier().IsMatch(name) ? name : null;
    }

    private static void Bases(Node node, UnitSymbol? symbol, CodeFileUnit unit)
    {
        if (symbol == null || symbol.Kind is not ("class" or "interface" or "trait")) return;
        // Tree-sitter names the base differently across languages. Only the
        // class header is visited; body references never become base edges.
        void Clause(Node clause)
        {
            foreach (var child in clause.NamedChildren)
            {
                switch (child.Type)
                {
                    case "class_heritage":
                    case "extends_clause":
                    case "extends_type_clause":
                    case "implements_clause":
                    case "base_clause":
                    case "class_interface_clause":
                        Clause(child);
                        continue;
                    case "type_arguments":
                    case "keyword_argument":
                    case "comment":
                        continue;
                }
                var target = BaseName(child);
                if (target == null) continue;
                var implementing = clause.Type.Contains("interface", StringComparison.Ordinal) || clause.Type == "implements_clause";
                unit.Refs.Add(new UnitRef { FromKey = symbol.Key, Relation = implementing ? "implements" : "inherits",
                    TargetName = target, Line = child.StartPosition.Row + 1 });
            }
        }
        foreach (var child in node.NamedChildren)
            if (child.Type is "argument_list" or "class_heritage" or "extends_clause" or "extends_type_clause"
                or "implements_clause" or "base_clause" or "class_interface_clause")
            {
                // Python's argument_list and JavaScript's bare class_heritage hold the bases directly.
                Clause(child);
            }
    }

    private static string? BaseName(Node node)
    {
        if (node.Type == "generic_type") return BaseName(node.GetChildForField("name") ?? node.NamedChildren.First());
        var text = node.Text;
        if (text.Length > MaxNameLength) return null;
        var dot = text.LastIndexOf('.');
        return Simple(dot >= 0 ? text[(dot + 1)..] : text);
    }

    private void Imports(Node node, string file, IRepoFileSet files, CodeFileUnit unit)
    {
        switch (node.Type)
        {
            case "import_statement" when Language == "python":
                PythonImport(node, file, files, unit);
                break;
            case "import_from_statement":
                PythonFromImport(node, file, files, unit);
                break;
            case "namespace_use_declaration":
                PhpUse(node, files, unit);
                break;
            case "import_statement":
                ScriptImport(node, file, files, unit, reExport: false);
                break;
            case "export_statement" when node.GetChildForField("source") != null:
                ScriptImport(node, file, files, unit, reExport: true);
                break;
            case "variable_declarator" when node.GetChildForField("value") is { Type: "call_expression" } call
                                            && call.GetChildForField("function")?.Text == "require":
                ScriptRequire(node, file, files, unit);
                break;
        }
    }

    private static void ScriptImport(Node node, string file, IRepoFileSet files, CodeFileUnit unit, bool reExport)
    {
        var source = node.Text;
        var module = Unquote(node.GetChildForField("source")?.Text);
        if (string.IsNullOrWhiteSpace(module)) return;
        var imp = new UnitImport { Line = node.StartPosition.Row + 1, Module = module, ResolvedFile = ResolveScript(file, module, files) };
        if (reExport)
        {
            // Names passed along are not names this file can call.
            imp.Wildcard = StarExport().IsMatch(source);
            unit.Imports.Add(imp);
            return;
        }
        if (NamespaceImport().Match(source) is { Success: true } ns) imp.Alias = ns.Groups[1].Value;
        if (DefaultImport().Match(source) is { Success: true } def) imp.Names.Add(new ImportedName { Name = def.Groups[1].Value });
        if (Braces().Match(source) is { Success: true } block) AddNames(imp, block.Groups[1].Value, " as ");
        unit.Imports.Add(imp);
    }

    private static void ScriptRequire(Node node, string file, IRepoFileSet files, CodeFileUnit unit)
    {
        var match = Require().Match(node.Text);
        if (!match.Success) return;
        var module = match.Groups[2].Value;
        var imp = new UnitImport { Line = node.StartPosition.Row + 1, Module = module, ResolvedFile = ResolveScript(file, module, files) };
        var bound = match.Groups[1].Value;
        if (bound.StartsWith('{')) AddNames(imp, bound.Trim('{', '}', ' '), ":");
        else imp.Alias = bound;
        unit.Imports.Add(imp);
    }

    /// <summary><c>a, b as c, type d</c>, or <c>a, b: c</c> from a destructured require.</summary>
    private static void AddNames(UnitImport imp, string list, string rename)
    {
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = raw.StartsWith("type ", StringComparison.Ordinal) ? raw[5..].Trim() : raw;
            var parts = entry.Split(rename, 2, StringSplitOptions.TrimEntries);
            var name = parts[0].Trim();
            if (!Identifier().IsMatch(name)) continue;
            var alias = parts.Length == 2 && Identifier().IsMatch(parts[1]) ? parts[1] : null;
            imp.Names.Add(new ImportedName { Name = name, Alias = alias });
        }
    }

    private static void PythonImport(Node node, string file, IRepoFileSet files, CodeFileUnit unit)
    {
        // import a.b as c, d: each one binds its own name.
        var list = node.Text;
        var start = list.IndexOf("import", StringComparison.Ordinal);
        if (start < 0) return;
        foreach (var part in list[(start + 6)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = PythonAs().Match(part);
            if (!pieces.Success) continue;
            var module = pieces.Groups[1].Value;
            unit.Imports.Add(new UnitImport { Line = node.StartPosition.Row + 1, Module = module,
                Alias = pieces.Groups[2].Success ? pieces.Groups[2].Value : null,
                ResolvedFile = ResolvePython(file, module, files) });
        }
    }

    private static void PythonFromImport(Node node, string file, IRepoFileSet files, CodeFileUnit unit)
    {
        var match = PythonFrom().Match(node.Text);
        if (!match.Success) return;
        var module = match.Groups[1].Value;
        var rest = LineComment().Replace(match.Groups[2].Value, "").Replace('(', ' ').Replace(')', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();
        var imp = new UnitImport { Line = node.StartPosition.Row + 1, Module = module, ResolvedFile = ResolvePython(file, module, files) };
        if (rest == "*")
        {
            imp.Wildcard = true;
            unit.Imports.Add(imp);
            return;
        }
        foreach (var part in rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = PythonAs().Match(part);
            if (!pieces.Success) continue;
            var name = pieces.Groups[1].Value;
            var alias = pieces.Groups[2].Success ? pieces.Groups[2].Value : null;
            // from pkg import store: the name can be a module of the package.
            var joined = module.EndsWith('.') ? module + name : module + "." + name;
            if (ResolvePython(file, joined, files) is { } submodule)
            {
                unit.Imports.Add(new UnitImport { Line = imp.Line, Module = joined, Alias = alias ?? name, ResolvedFile = submodule });
                continue;
            }
            imp.Names.Add(new ImportedName { Name = name, Alias = alias });
        }
        if (imp.Names.Count > 0 || imp.ResolvedFile != null) unit.Imports.Add(imp);
    }

    private static void PhpUse(Node node, IRepoFileSet files, CodeFileUnit unit)
    {
        var source = PhpUseKeyword().Replace(node.Text.Trim().TrimEnd(';'), "").Trim();
        void One(string name, string? alias)
        {
            var module = name.Trim().Trim('\\');
            if (module.Length == 0) return;
            var last = module[(module.LastIndexOf('\\') + 1)..];
            var imp = new UnitImport { Line = node.StartPosition.Row + 1, Module = module, ResolvedFile = ResolvePhp(module, files) };
            imp.Names.Add(new ImportedName { Name = last, Alias = alias });
            unit.Imports.Add(imp);
        }
        var group = source.IndexOf('{');
        if (group >= 0)
        {
            var prefix = source[..group].Trim().TrimEnd('\\');
            foreach (var part in source[(group + 1)..].TrimEnd('}').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pieces = part.Split(" as ", 2, StringSplitOptions.TrimEntries);
                One(prefix + "\\" + pieces[0], pieces.Length == 2 ? pieces[1] : null);
            }
            return;
        }
        foreach (var part in source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(" as ", 2, StringSplitOptions.TrimEntries);
            One(pieces[0], pieces.Length == 2 ? pieces[1] : null);
        }
    }

    private static string? Unquote(string? text) => text?.Trim().Trim('\'', '"', '`');

    private static string? ResolveScript(string file, string module, IRepoFileSet files)
    {
        string? stem;
        if (module.StartsWith("./", StringComparison.Ordinal) || module.StartsWith("../", StringComparison.Ordinal) || module is "." or "..")
            stem = Walk(file, module);
        else if (module.StartsWith("@/", StringComparison.Ordinal) || module.StartsWith("~/", StringComparison.Ordinal))
        {
            // The usual path alias. It can only name a file that exists, so a
            // repo that points it elsewhere loses the edge, not its accuracy.
            var tail = module[2..];
            return First(files, "src/" + tail) ?? First(files, tail);
        }
        else return null;
        return stem == null ? null : First(files, stem);
    }

    private static string? First(IRepoFileSet files, string stem)
    {
        // TypeScript with ES modules writes ./x.js for the file x.ts.
        var bare = Regex.Replace(stem, @"\.(m|c)?jsx?$", "");
        foreach (var candidate in new[] { stem, stem + ".ts", stem + ".tsx", stem + ".js", stem + ".jsx", stem + ".mjs", stem + ".cjs", stem + ".mts",
                     bare + ".ts", bare + ".tsx", stem + "/index.ts", stem + "/index.tsx", stem + "/index.js", stem + "/index.jsx" })
            if (files.Contains(candidate)) return candidate;
        return null;
    }

    private static string? ResolvePython(string file, string module, IRepoFileSet files)
    {
        if (module.StartsWith('.'))
        {
            var dots = module.TakeWhile(c => c == '.').Count();
            var stem = Walk(file, "./" + string.Concat(Enumerable.Repeat("../", dots - 1)) + module[dots..].Replace('.', '/'));
            return stem == null ? null : PythonFile(stem, files);
        }
        var path = module.Replace('.', '/');
        if ((PythonFile(path, files) ?? PythonFile("src/" + path, files)) is { } direct) return direct;
        // A package below the repo root: one file with that tail, or nothing.
        var tail = "/" + path + ".py";
        var named = files.Named(path[(path.LastIndexOf('/') + 1)..] + ".py").Where(f => f.EndsWith(tail, StringComparison.Ordinal)).Take(2).ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private static string? PythonFile(string stem, IRepoFileSet files)
    {
        foreach (var candidate in new[] { stem + ".py", stem + ".pyi", stem + "/__init__.py" })
            if (files.Contains(candidate)) return candidate;
        return null;
    }

    private static string? ResolvePhp(string module, IRepoFileSet files)
    {
        var path = module.Replace('\\', '/');
        var lowered = char.ToLowerInvariant(path[0]) + path[1..];
        foreach (var candidate in new[] { path + ".php", lowered + ".php", "src/" + path + ".php", "app/" + path + ".php" })
            if (files.Contains(candidate)) return candidate;
        // PSR-4 maps a prefix to any folder: the longest matching tail decides.
        var file = path[(path.LastIndexOf('/') + 1)..] + ".php";
        var parts = path.Split('/');
        var best = files.Named(file)
            .Select(f => (File: f, Score: Common(f.Split('/'), parts)))
            .OrderByDescending(x => x.Score).Take(2).ToList();
        return best.Count == 0 || (best.Count == 2 && best[0].Score == best[1].Score) ? null : best[0].File;
    }

    private static int Common(string[] file, string[] module)
    {
        var score = 0;
        for (int f = file.Length - 2, m = module.Length - 2; f >= 0 && m >= 0; f--, m--)
        {
            if (!file[f].Equals(module[m], StringComparison.OrdinalIgnoreCase)) break;
            score++;
        }
        return score;
    }

    /// <summary>Resolve a ./ or ../ path against the importing file. Null when it climbs out of the repo.</summary>
    private static string? Walk(string file, string relative)
    {
        var dir = file.Split('/')[..^1].ToList();
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (dir.Count == 0) return null; dir.RemoveAt(dir.Count - 1); }
            else dir.Add(segment);
        }
        return string.Join('/', dir);
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z_0-9]*$")]
    private static partial Regex Identifier();
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z_0-9]*(\.[A-Za-z_][A-Za-z_0-9]*)*$")]
    private static partial Regex DottedPath();
    [GeneratedRegex(@"\bexport\s+\*")]
    private static partial Regex StarExport();
    [GeneratedRegex(@"\*\s+as\s+([A-Za-z_$][\w$]*)")]
    private static partial Regex NamespaceImport();
    [GeneratedRegex(@"^\s*import\s+(?:type\s+)?([A-Za-z_$][\w$]*)\s*(?:,|from\b)")]
    private static partial Regex DefaultImport();
    [GeneratedRegex(@"\{([^}]*)\}")]
    private static partial Regex Braces();
    [GeneratedRegex(@"^\s*(\{[^}]*\}|[A-Za-z_$][\w$]*)\s*=\s*require\(\s*['""`]([^'""`]+)['""`]\s*\)\s*$")]
    private static partial Regex Require();
    [GeneratedRegex(@"^([\w.]+)(?:\s+as\s+([A-Za-z_]\w*))?$")]
    private static partial Regex PythonAs();
    [GeneratedRegex(@"^\s*from\s+(\.*[\w.]*)\s+import\s+(.*)$", RegexOptions.Singleline)]
    private static partial Regex PythonFrom();
    [GeneratedRegex(@"#[^\n]*")]
    private static partial Regex LineComment();
    [GeneratedRegex(@"^use\s+(?:function\s+|const\s+)?")]
    private static partial Regex PhpUseKeyword();
}
