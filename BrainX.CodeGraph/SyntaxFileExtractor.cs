using System.Text.RegularExpressions;
using TreeSitter;

namespace BrainX.CodeGraph;

/// <summary>
/// Tree-sitter declaration and reference pass for Graphify's other language
/// families. The AST supplies sites and lexical containers; NameResolver places
/// names conservatively and marks non-local matches INFERRED or AMBIGUOUS.
/// </summary>
public sealed class SyntaxFileExtractor : IFileExtractor
{
    private readonly string _grammar;
    public string Language { get; }
    public IReadOnlyCollection<string> Extensions { get; }
    public string Version => "tree-sitter/1+brainx-syntax/1";

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
        var declared = new Dictionary<Node, UnitSymbol>();

        void Declare(Node node, UnitSymbol? parent)
        {
            var (name, kind) = Declaration(node);
            var current = parent;
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
                declared[node] = current;
            }
            foreach (var child in node.NamedChildren) Declare(child, current);
        }
        Declare(root, null);

        void References(Node node, UnitSymbol? parent)
        {
            if (declared.TryGetValue(node, out var found)) parent = found;
            if (CallKind(node.Type) is { } relation)
            {
                var target = CallTarget(node);
                var (name, receiver) = TargetName(target);
                if (!string.IsNullOrEmpty(name)) unit.Refs.Add(new UnitRef { FromKey = parent?.Key,
                    Relation = relation, TargetName = name, Receiver = receiver, Line = target?.StartPosition.Row + 1 ?? node.StartPosition.Row + 1 });
            }
            if (node.Type is "import_statement" or "import_from_statement" or "namespace_use_declaration")
                Import(node, relativePath, repoFiles, unit);
            if (node.Type is "class_definition" or "class_declaration" or "interface_declaration" or "trait_declaration")
                Bases(node, parent, unit);
            foreach (var child in node.NamedChildren) References(child, parent);
        }
        References(root, null);
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
            "method_definition" or "method_declaration" => "method",
            _ => ""
        };
        if (kind.Length == 0 && type == "variable_declarator" &&
            node.GetChildForField("value")?.Type is "arrow_function" or "function_expression" or "function") kind = "function";
        if (kind.Length == 0) return (null, "");
        var name = node.GetChildForField("name")?.Text;
        name = name?.Trim(' ', '\t', '\r', '\n', '$', '\'', '"');
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z_0-9]*$")) return (null, "");
        return (name, kind);
    }

    private string? CallKind(string type) => type switch
    {
        "call_expression" or "call" or "function_call_expression" or "member_call_expression" or "scoped_call_expression" => "calls",
        "new_expression" or "object_creation_expression" => "creates",
        _ => null
    };

    private static Node? CallTarget(Node node) => node.GetChildForField("function")
        ?? node.GetChildForField("constructor") ?? node.GetChildForField("class")
        ?? node.GetChildForField("name") ?? node.NamedChildren.FirstOrDefault();

    private static (string? Name, string? Receiver) TargetName(Node? target)
    {
        if (target == null) return (null, null);
        if (target.Type is "member_expression" or "attribute" or "member_call_expression" or "scoped_call_expression")
        {
            var name = target.GetChildForField("property")?.Text ?? target.GetChildForField("attribute")?.Text
                ?? target.GetChildForField("name")?.Text ?? target.NamedChildren.LastOrDefault()?.Text;
            var receiver = target.GetChildForField("object")?.Text ?? target.GetChildForField("scope")?.Text
                ?? target.NamedChildren.FirstOrDefault()?.Text;
            return (Simple(name), Simple(receiver));
        }
        return (Simple(target.Text), null);
    }

    private static string? Simple(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var name = text.Trim().TrimStart('$');
        return Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z_0-9]*$") ? name : null;
    }

    private void Bases(Node node, UnitSymbol? symbol, CodeFileUnit unit)
    {
        if (symbol == null || symbol.Kind is not ("class" or "interface" or "trait")) return;
        // Tree-sitter names the base differently across languages. Only the
        // class header is visited; body references never become base edges.
        foreach (var child in node.NamedChildren)
        {
            if (child.Type is not ("argument_list" or "class_heritage" or "extends_clause" or "base_clause" or "class_interface_clause")) continue;
            foreach (var name in child.NamedChildren)
            {
                var target = Simple(name.Text);
                if (target == null) continue;
                unit.Refs.Add(new UnitRef { FromKey = symbol.Key,
                    Relation = child.Type.Contains("interface", StringComparison.Ordinal) ? "implements" : "inherits",
                    TargetName = target, Line = name.StartPosition.Row + 1 });
            }
        }
    }

    private static void Import(Node node, string file, IRepoFileSet files, CodeFileUnit unit)
    {
        var source = node.Text;
        string? module = null;
        if (node.Type == "import_statement" && source.Contains(" from ", StringComparison.Ordinal))
            module = Regex.Match(source, @"\bfrom\s+['""']([^'""']+)['""']").Groups[1].Value;
        else if (node.Type == "import_statement")
        {
            module = Regex.Match(source, @"\bimport\s+['""']([^'""']+)['""']").Groups[1].Value;
            if (module.Length == 0) module = Regex.Match(source, @"\bimport\s+([\w.]+)").Groups[1].Value;
        }
        else if (node.Type == "import_from_statement")
            module = Regex.Match(source, @"\bfrom\s+([\w.]+)\s+import\b").Groups[1].Value;
        else if (node.Type == "namespace_use_declaration")
            module = Regex.Match(source, @"\buse\s+([^;]+)").Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(module)) return;
        var imp = new UnitImport { Line = node.StartPosition.Row + 1, Module = module,
            ResolvedFile = ResolveModule(file, module, files) };
        if (source.Contains('{'))
            foreach (Match m in Regex.Matches(source, @"([A-Za-z_][\w]*)(?:\s+as\s+([A-Za-z_][\w]*))?"))
                if (m.Groups[1].Value is not ("import" or "from" or "as"))
                    imp.Names.Add(new ImportedName { Name = m.Groups[1].Value,
                        Alias = m.Groups[2].Success ? m.Groups[2].Value : null });
        else if (node.Type == "import_from_statement")
        {
            var rest = source[(source.IndexOf("import", StringComparison.Ordinal) + 6)..];
            foreach (Match nameMatch in Regex.Matches(rest, @"([A-Za-z_][\w]*)(?:\s+as\s+([A-Za-z_][\w]*))?"))
                imp.Names.Add(new ImportedName { Name = nameMatch.Groups[1].Value,
                    Alias = nameMatch.Groups[2].Success ? nameMatch.Groups[2].Value : null });
        }
        else if (Regex.Match(source, @"\*\s+as\s+([A-Za-z_][\w]*)") is { Success: true } alias)
            imp.Alias = alias.Groups[1].Value;
        unit.Imports.Add(imp);
    }

    private static string? ResolveModule(string file, string module, IRepoFileSet files)
    {
        if (!module.StartsWith('.')) return null;
        var dir = file.Split('/')[..^1].ToList();
        string part;
        if (module.StartsWith("./") || module.StartsWith("../")) part = module;
        else
        {
            var dots = module.TakeWhile(c => c == '.').Count();
            part = string.Concat(Enumerable.Repeat("../", Math.Max(0, dots - 1))) + module[dots..].Replace('.', '/');
        }
        foreach (var segment in part.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (dir.Count == 0) return null; dir.RemoveAt(dir.Count - 1); }
            else dir.Add(segment);
        }
        var stem = string.Join('/', dir);
        foreach (var candidate in new[] { stem, stem + ".ts", stem + ".tsx", stem + ".js", stem + ".jsx", stem + ".py", stem + ".php", stem + "/index.ts", stem + "/index.js", stem + "/__init__.py" })
            if (files.Contains(candidate)) return candidate;
        return null;
    }
}
