using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace BrainX.CodeGraph.CSharp;

/// <summary>One file's declarations and bound references, read through its compilation's semantic model.</summary>
internal sealed class FileWalker
{
    private static readonly SymbolDisplayFormat ShortType = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.ExpandNullable);

    private readonly SyntaxTree _tree;
    private readonly SemanticModel _model;
    private readonly CodeFileUnit _unit;
    private readonly HashSet<SyntaxTree> _repoTrees;
    private readonly HashSet<string> _memberNames;
    private readonly CancellationToken _ct;
    private readonly Dictionary<SyntaxNode, string> _keyByNode = new();
    private readonly Dictionary<ISymbol, string> _localKeys = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<string, UnitSymbol> _byKey = new(StringComparer.Ordinal);

    public FileWalker(Compilation compilation, SyntaxTree tree, CodeFileUnit unit, HashSet<SyntaxTree> repoTrees, HashSet<string> memberNames, CancellationToken ct)
    {
        _tree = tree;
        _model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        _unit = unit;
        _repoTrees = repoTrees;
        _memberNames = memberNames;
        _ct = ct;
    }

    public void Run()
    {
        var root = _tree.GetCompilationUnitRoot(_ct);
        _unit.LineCount = _tree.GetText(_ct).Lines.Count;
        _unit.HasErrors = _tree.GetDiagnostics(_ct).Any(d => d.Severity == DiagnosticSeverity.Error);

        foreach (var node in root.DescendantNodes()) Declare(node);
        foreach (var node in root.DescendantNodes()) Reference(node);
    }

    // ── declarations ──

    private void Declare(SyntaxNode node)
    {
        switch (node)
        {
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                if (_model.GetDeclaredSymbol(node, _ct) is INamedTypeSymbol declared)
                {
                    var key = Add(node, declared, KindOf(declared), declared.Name, Qualify(declared.ContainingType, declared.Name), declared.ToDisplayString());
                    if (key != null) DeclareBases(node, declared, key);
                }
                break;
            case MethodDeclarationSyntax or ConstructorDeclarationSyntax or DestructorDeclarationSyntax
                or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax:
                if (_model.GetDeclaredSymbol(node, _ct) is IMethodSymbol m)
                {
                    var name = MethodName(node, m);
                    var key = Add(node, m, m.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor ? "constructor" : "method",
                                  name, Qualify(m.ContainingType, name), Signature(name, m.Parameters));
                    if (key != null) DeclareOverride(node, m.IsOverride ? m.OverriddenMethod : null, key);
                }
                break;
            case PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax:
                if (_model.GetDeclaredSymbol(node, _ct) is { } member)
                {
                    var name = node is IndexerDeclarationSyntax ? "this[]" : member.Name;
                    var (kind, type) = member switch
                    {
                        IPropertySymbol p => ("property", p.Type),
                        IEventSymbol e => ("event", e.Type),
                        _ => ("property", (ITypeSymbol?)null)
                    };
                    var sig = member is IPropertySymbol { IsIndexer: true } ix ? Signature(name, ix.Parameters) : $"{name} : {type?.ToDisplayString(ShortType)}";
                    var key = Add(node, member, kind, name, Qualify(member.ContainingType, name), sig);
                    if (key != null)
                        DeclareOverride(node, member switch
                        {
                            IPropertySymbol { IsOverride: true } p => p.OverriddenProperty,
                            IEventSymbol { IsOverride: true } e => e.OverriddenEvent,
                            _ => null
                        }, key);
                }
                break;
            case VariableDeclaratorSyntax v when v.Parent?.Parent is BaseFieldDeclarationSyntax:
                if (_model.GetDeclaredSymbol(v, _ct) is { } field and (IFieldSymbol or IEventSymbol))
                {
                    var fieldType = field is IFieldSymbol f ? f.Type : ((IEventSymbol)field).Type;
                    Add(v, field, field is IEventSymbol ? "event" : "field", field.Name, Qualify(field.ContainingType, field.Name), $"{field.Name} : {fieldType.ToDisplayString(ShortType)}");
                }
                break;
            case EnumMemberDeclarationSyntax em:
                if (_model.GetDeclaredSymbol(em, _ct) is IFieldSymbol ef)
                    Add(em, ef, "field", ef.Name, Qualify(ef.ContainingType, ef.Name), null);
                break;
            case LocalFunctionStatementSyntax lf:
                if (_model.GetDeclaredSymbol(lf, _ct) is IMethodSymbol local)
                {
                    var parent = ParentKey(lf);
                    var parentQ = parent != null && _byKey.TryGetValue(parent, out var ps) ? ps.Qualified ?? ps.Name : null;
                    // Local functions have no documentation id; their key hangs off the member they live in.
                    var baseKey = (parent ?? _unit.Path) + "/" + local.Name;
                    var k = baseKey;
                    for (var i = 2; _byKey.ContainsKey(k); i++) k = baseKey + "#" + i;
                    _localKeys[local] = k;
                    Add(lf, local, "function", local.Name, parentQ == null ? null : parentQ + "." + local.Name, Signature(local.Name, local.Parameters), k);
                }
                break;
        }
    }

    private string? Add(SyntaxNode node, ISymbol symbol, string kind, string name, string? qualified, string? signature, string? key = null)
    {
        key ??= KeyOf(symbol);
        if (key == null) return null;
        _keyByNode[node] = key;
        // Partial method or property halves in one file share a key: keep the first.
        if (_byKey.ContainsKey(key)) return key;
        var (start, end) = Lines(node);
        var s = new UnitSymbol
        {
            Key = key, Name = name, Qualified = qualified, Kind = kind, Line = start, EndLine = end,
            ParentKey = ParentKey(node), Signature = signature
        };
        _byKey[key] = s;
        _unit.Symbols.Add(s);
        return key;
    }

    private void DeclareBases(SyntaxNode node, INamedTypeSymbol type, string key)
    {
        if (node is BaseTypeDeclarationSyntax { BaseList: { } bases })
            foreach (var b in bases.Types)
            {
                var t = _model.GetTypeInfo(b.Type, _ct).Type as INamedTypeSymbol;
                if (t == null || t.TypeKind == TypeKind.Error) { Unbound("inherits"); continue; }
                var relation = t.TypeKind == TypeKind.Interface && type.TypeKind != TypeKind.Interface ? "implements" : "inherits";
                Bound(key, relation, t.OriginalDefinition, Line(b.Type));
            }

        // Each member this part declares that implements an interface member.
        foreach (var iface in type.AllInterfaces)
        {
            if (!InRepo(iface.OriginalDefinition)) continue;
            foreach (var im in iface.GetMembers())
            {
                if (im is not (IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol) || im.IsStatic) continue;
                ISymbol? impl;
                try { impl = type.FindImplementationForInterfaceMember(im); }
                catch (InvalidOperationException) { continue; }
                if (impl == null || !SymbolEqualityComparer.Default.Equals(impl.ContainingType, type)) continue;
                var decl = impl.DeclaringSyntaxReferences.FirstOrDefault(r => r.SyntaxTree == _tree);
                if (decl == null || KeyOf(impl) is not { } implKey) continue;
                Bound(implKey, "implements", im.OriginalDefinition, Lines(decl.GetSyntax(_ct)).Start);
            }
        }
    }

    private void DeclareOverride(SyntaxNode node, ISymbol? overridden, string key)
    {
        if (overridden == null) return;
        var name = node switch
        {
            MethodDeclarationSyntax m => m.Identifier,
            PropertyDeclarationSyntax p => p.Identifier,
            EventDeclarationSyntax e => e.Identifier,
            _ => node.GetFirstToken()
        };
        Bound(key, "overrides", overridden.OriginalDefinition, Line(name));
    }

    // ── references ──

    private void Reference(SyntaxNode node)
    {
        switch (node)
        {
            case InvocationExpressionSyntax inv:
            {
                if (inv.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" } && _model.GetSymbolInfo(inv, _ct).Symbol == null) return;
                var nameNode = InvokedName(inv.Expression);
                var info = _model.GetSymbolInfo(inv, _ct);
                Handle(inv, "calls", info, nameNode, s => s is IMethodSymbol { MethodKind: not MethodKind.DelegateInvoke } m ? Method(m) : null);
                break;
            }
            case BaseObjectCreationExpressionSyntax create:
            {
                var info = _model.GetSymbolInfo(create, _ct);
                SyntaxNode at = create is ObjectCreationExpressionSyntax oc ? oc.Type : create;
                Handle(create, "creates", info, at, s => (s as IMethodSymbol)?.ContainingType?.OriginalDefinition ?? s as INamedTypeSymbol);
                break;
            }
            case ConstructorInitializerSyntax ci:
            {
                var info = _model.GetSymbolInfo(ci, _ct);
                Handle(ci, "calls", info, ci.ThisOrBaseKeyword.Parent!, s => s is IMethodSymbol { IsImplicitlyDeclared: false } m ? m.OriginalDefinition : null);
                break;
            }
            case PrimaryConstructorBaseTypeSyntax pb:
            {
                var info = _model.GetSymbolInfo(pb, _ct);
                Handle(pb, "calls", info, pb.Type, s => s is IMethodSymbol { IsImplicitlyDeclared: false } m ? m.OriginalDefinition : null);
                break;
            }
            case SimpleNameSyntax name when _memberNames.Contains(name.Identifier.ValueText) && !IsHandledElsewhere(name):
            {
                var info = _model.GetSymbolInfo(name, _ct);
                var s = info.Symbol;
                if (s == null)
                {
                    // A method group the compiler could not narrow: leave it, as an invocation would have said.
                    return;
                }
                var target = s switch
                {
                    IPropertySymbol p => p.OriginalDefinition,
                    IFieldSymbol { IsImplicitlyDeclared: false } f => f.OriginalDefinition,
                    IEventSymbol e => e.OriginalDefinition,
                    IMethodSymbol m when m.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction or MethodKind.ReducedExtension => Method(m),
                    _ => null
                };
                if (target == null) return;
                if (!InRepo(target) && !IsLocal(target)) { External("uses"); return; }
                Bound(From(name), "uses", target, Line(name));
                break;
            }
        }
    }

    private void Handle(SyntaxNode site, string relation, SymbolInfo info, SyntaxNode at, Func<ISymbol, ISymbol?> map)
    {
        var from = From(site);
        if (info.Symbol is { } s)
        {
            var target = map(s);
            if (target == null) return;
            if (!InRepo(target) && !IsLocal(target)) { External(relation); return; }
            Bound(from, relation, target, Line(at));
            return;
        }
        if (info.CandidateSymbols.Length > 0)
        {
            var mapped = info.CandidateSymbols.Select(map).Where(t => t != null).Select(t => t!).ToList();
            var inRepo = mapped.Where(t => InRepo(t) || IsLocal(t)).Select(KeyOf).Where(k => k != null).Distinct(StringComparer.Ordinal).Take(5).ToList();
            if (inRepo.Count == 0)
            {
                if (mapped.Count > 0) External(relation); else Unbound(relation);
                return;
            }
            _unit.Refs.Add(new UnitRef { FromKey = from, Relation = relation, CandidateKeys = inRepo!, TargetName = NameOf(at), Line = Line(at) });
            return;
        }
        Unbound(relation);
    }

    private void Bound(string? from, string relation, ISymbol target, int line)
    {
        var key = KeyOf(target);
        if (key == null) return;
        _unit.Refs.Add(new UnitRef { FromKey = from, Relation = relation, TargetKey = key, TargetName = target.Name, Line = line });
    }

    private void External(string relation) => _unit.External[relation] = _unit.External.GetValueOrDefault(relation) + 1;

    private void Unbound(string relation) => _unit.UnboundCounts[relation] = _unit.UnboundCounts.GetValueOrDefault(relation) + 1;

    /// <summary>The member a reference is written in: the nearest declaration above it, or the file.</summary>
    private string? From(SyntaxNode node)
    {
        for (var n = node.Parent; n != null; n = n.Parent)
            if (_keyByNode.TryGetValue(n, out var k)) return k;
        return null;
    }

    private string? ParentKey(SyntaxNode node)
    {
        for (var n = node.Parent; n != null; n = n.Parent)
        {
            // A field's declarator sits under its declaration, which is not a symbol.
            if (n is BaseFieldDeclarationSyntax or VariableDeclarationSyntax) continue;
            if (_keyByNode.TryGetValue(n, out var k)) return k;
        }
        return null;
    }

    private bool IsHandledElsewhere(SimpleNameSyntax name)
    {
        var parent = name.Parent;
        // The name an invocation calls is bound with the invocation.
        if (parent is InvocationExpressionSyntax inv && inv.Expression == name) return true;
        if (parent is MemberAccessExpressionSyntax ma && ma.Name == name && ma.Parent is InvocationExpressionSyntax inv2 && inv2.Expression == ma) return true;
        if (parent is MemberBindingExpressionSyntax mb && mb.Name == name && mb.Parent is InvocationExpressionSyntax) return true;
        // Declarations, nameof, and the type in a creation are not uses of a member.
        for (var n = parent; n != null; n = n.Parent)
        {
            if (n is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } }) return true;
            if (n is StatementSyntax or MemberDeclarationSyntax) break;
        }
        return parent is ObjectCreationExpressionSyntax or NameColonSyntax or NameEqualsSyntax { Parent: AnonymousObjectMemberDeclaratorSyntax } or QualifiedNameSyntax or UsingDirectiveSyntax;
    }

    private bool InRepo(ISymbol s)
    {
        foreach (var loc in s.Locations)
            if (loc.IsInSource && loc.SourceTree != null && _repoTrees.Contains(loc.SourceTree)) return true;
        return false;
    }

    private bool IsLocal(ISymbol s) => _localKeys.ContainsKey(s);

    private string? KeyOf(ISymbol s)
    {
        if (_localKeys.TryGetValue(s, out var local)) return local;
        if (s is IMethodSymbol { MethodKind: MethodKind.LocalFunction }) return null;
        var def = s.OriginalDefinition;
        // Two projects can each have Ns.Program. They are different symbols, and
        // the documentation id alone would fold them into one.
        return def.GetDocumentationCommentId() is { } id ? def.ContainingAssembly?.Name + "|" + id : null;
    }

    private static ISymbol Method(IMethodSymbol m)
    {
        var def = (m.ReducedFrom ?? m).OriginalDefinition;
        // A call binds to a partial method's declaration; both halves share the key.
        return def.PartialDefinitionPart ?? def;
    }

    private static string KindOf(INamedTypeSymbol t) => t.TypeKind switch
    {
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        TypeKind.Struct => t.IsRecord ? "record" : "struct",
        _ => t.IsRecord ? "record" : "class"
    };

    private static string MethodName(SyntaxNode node, IMethodSymbol m) => node switch
    {
        ConstructorDeclarationSyntax => m.ContainingType.Name,
        DestructorDeclarationSyntax => "~" + m.ContainingType.Name,
        OperatorDeclarationSyntax op => "operator " + op.OperatorToken.ValueText,
        ConversionOperatorDeclarationSyntax conv => "operator " + conv.Type,
        _ => m.Name
    };

    private static string? Qualify(INamedTypeSymbol? container, string name)
    {
        if (container == null) return null;
        var parts = new List<string> { name };
        for (var c = container; c != null; c = c.ContainingType) parts.Insert(0, c.Name);
        return string.Join(".", parts);
    }

    private static string Signature(string name, IEnumerable<IParameterSymbol> parameters) =>
        $"{name}({string.Join(", ", parameters.Select(p => p.Type.ToDisplayString(ShortType)))})";

    private static SyntaxNode InvokedName(ExpressionSyntax e) => e switch
    {
        MemberAccessExpressionSyntax ma => ma.Name,
        MemberBindingExpressionSyntax mb => mb.Name,
        _ => e
    };

    private static string? NameOf(SyntaxNode n) => n switch
    {
        SimpleNameSyntax s => s.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
        _ => null
    };

    private int Line(SyntaxNode n) => _tree.GetLineSpan(new TextSpan(n.SpanStart, 0)).StartLinePosition.Line + 1;
    private int Line(SyntaxToken t) => _tree.GetLineSpan(new TextSpan(t.SpanStart, 0)).StartLinePosition.Line + 1;

    /// <summary>From the first token after the attributes to the end, so a method cites its signature line, not its [Attribute].</summary>
    private (int Start, int End) Lines(SyntaxNode node)
    {
        var start = node.SpanStart;
        var attrs = node switch
        {
            MemberDeclarationSyntax m => m.AttributeLists,
            LocalFunctionStatementSyntax lf => lf.AttributeLists,
            _ => default
        };
        if (attrs.Count > 0)
        {
            var next = attrs.Last().GetLastToken().GetNextToken();
            if (next.SpanStart < node.Span.End) start = next.SpanStart;
        }
        var s = _tree.GetLineSpan(new TextSpan(start, 0)).StartLinePosition.Line + 1;
        var e = _tree.GetLineSpan(node.Span).EndLinePosition.Line + 1;
        return (s, Math.Max(s, e));
    }
}
