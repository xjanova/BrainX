namespace BrainX.CodeGraph;

/// <summary>
/// What one extractor read from one file: its declarations and the references
/// they make, before anything is linked across files. Units are cached per
/// file, so a refresh re-reads only the files that changed and then re-links
/// the whole graph, which is dictionary work and takes milliseconds.
///
/// A reference names its target one of two ways. A compiler-backed extractor
/// (Roslyn) binds it and fills <see cref="UnitRef.TargetKey"/>: EXTRACTED,
/// no guessing. A syntax-only extractor (tree-sitter) fills the name as
/// written plus its receiver and the file's imports, and
/// <see cref="NameResolver"/> resolves it with Graphify's rules: INFERRED, or
/// AMBIGUOUS when more than one candidate survives.
/// </summary>
public sealed class CodeFileUnit
{
    /// <summary>Repo-relative, '/' separated.</summary>
    public string Path { get; set; } = string.Empty;
    /// <summary>csharp · typescript · tsx · javascript · python · php.</summary>
    public string Language { get; set; } = string.Empty;
    /// <summary>What read it and at which version, e.g. <c>roslyn/5.9.0</c>.</summary>
    public string Extractor { get; set; } = string.Empty;
    public long Length { get; set; }
    public DateTime WrittenAt { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public int LineCount { get; set; }
    /// <summary>The parser met a syntax error. What it could read is kept; the file is reported.</summary>
    public bool HasErrors { get; set; }
    /// <summary>The file was too large or binary and was listed, not read.</summary>
    public string? Skipped { get; set; }
    public List<UnitSymbol> Symbols { get; set; } = [];
    public List<UnitRef> Refs { get; set; } = [];
    public List<UnitImport> Imports { get; set; } = [];
    /// <summary>
    /// Names this file used that its extractor could not bind (C#). A later
    /// declaration with one of these names re-reads the file, so a call to a
    /// method that did not exist yet is picked up once it does.
    /// </summary>
    public List<string> Unbound { get; set; } = [];
    /// <summary>References a compiler-backed extractor bound outside the repo (framework, packages), per relation. Counted, not stored.</summary>
    public Dictionary<string, int> External { get; set; } = new(StringComparer.Ordinal);
    /// <summary>References a compiler-backed extractor could not bind at all, per relation.</summary>
    public Dictionary<string, int> UnboundCounts { get; set; } = new(StringComparer.Ordinal);
}

public sealed class UnitSymbol
{
    /// <summary>
    /// Stable across line moves and unique within the repo for one
    /// declaration: Roslyn's documentation id for C#
    /// (<c>M:Ns.Type.Method(System.String)</c>), <c>path#Container.name</c> for
    /// the rest. Parts of one C# partial type share a key and are merged.
    /// </summary>
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Container-qualified without the namespace, e.g. <c>Program.ScoreNode</c>. Null at top level.</summary>
    public string? Qualified { get; set; }
    /// <summary>See <see cref="BrainX.Core.Services.CodeSymbol.Kind"/>.</summary>
    public string Kind { get; set; } = "unknown";
    /// <summary>1-based, first line of the declaration (attributes and doc comments excluded).</summary>
    public int Line { get; set; }
    /// <summary>1-based, last line of the declaration.</summary>
    public int EndLine { get; set; }
    /// <summary>The containing symbol's key; null when the file itself contains it.</summary>
    public string? ParentKey { get; set; }
    public string? Signature { get; set; }
}

public sealed class UnitRef
{
    /// <summary>The symbol the reference is written in (a key of this file's <see cref="CodeFileUnit.Symbols"/>), or null for file-level code.</summary>
    public string? FromKey { get; set; }
    /// <summary>
    /// calls · creates · inherits · implements · overrides · uses. File-to-file
    /// imports are not references: they come from <see cref="CodeFileUnit.Imports"/>.
    /// </summary>
    public string Relation { get; set; } = string.Empty;
    /// <summary>Bound by the extractor: the target's <see cref="UnitSymbol.Key"/>. EXTRACTED.</summary>
    public string? TargetKey { get; set; }
    /// <summary>The compiler found several candidates and chose none (C# overload failure). Keys, at most five.</summary>
    public List<string>? CandidateKeys { get; set; }
    /// <summary>The name as written (<c>run</c> in <c>this.run()</c>), for name resolution.</summary>
    public string? TargetName { get; set; }
    /// <summary>What it was called on, as written: <c>this</c>, <c>self</c>, <c>super</c>, <c>parent</c>, a module alias, a class name, or null.</summary>
    public string? Receiver { get; set; }
    /// <summary>1-based line of the target's name at the site.</summary>
    public int Line { get; set; }
}

public sealed class UnitImport
{
    public int Line { get; set; }
    /// <summary>As written: <c>./store</c>, <c>os.path</c>, <c>App\Models\User</c>.</summary>
    public string Module { get; set; } = string.Empty;
    /// <summary>The repo file it names, when the extractor could place it.</summary>
    public string? ResolvedFile { get; set; }
    /// <summary>Names imported from it; empty means the module itself (<c>import os</c>).</summary>
    public List<ImportedName> Names { get; set; } = [];
    /// <summary>The local name of the whole module: <c>import * as m</c>, <c>import os.path as p</c>, <c>use A\B as C</c>.</summary>
    public string? Alias { get; set; }
    /// <summary><c>from m import *</c> / <c>export * from</c>.</summary>
    public bool Wildcard { get; set; }
}

public sealed class ImportedName
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Local name when renamed (<c>import { a as b }</c>); null otherwise.</summary>
    public string? Alias { get; set; }
}

/// <summary>A language a syntax-only extractor reads one file at a time.</summary>
public interface IFileExtractor
{
    /// <summary>csharp · typescript · tsx · javascript · python · php.</summary>
    string Language { get; }
    /// <summary>Lower-case extensions with the dot.</summary>
    IReadOnlyCollection<string> Extensions { get; }
    /// <summary>Recorded on each unit; a change re-reads every file of the language.</summary>
    string Version { get; }
    /// <summary>
    /// Read one file. <paramref name="relativePath"/> is repo-relative with '/';
    /// <paramref name="repoFiles"/> lets import specifiers be placed on real files.
    /// Must not throw on bad syntax: set <see cref="CodeFileUnit.HasErrors"/> and keep what parsed.
    /// </summary>
    CodeFileUnit Extract(string relativePath, string text, IRepoFileSet repoFiles);
}

/// <summary>The files of the repo being read, for placing import specifiers.</summary>
public interface IRepoFileSet
{
    bool Contains(string relativePath);
    IEnumerable<string> All { get; }
}
