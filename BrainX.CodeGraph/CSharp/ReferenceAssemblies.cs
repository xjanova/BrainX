using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;

namespace BrainX.CodeGraph.CSharp;

/// <summary>
/// The framework assemblies a project compiles against: the targeting packs of
/// an installed .NET SDK (packs/Microsoft.NETCore.App.Ref/9.0.x/ref/net9.0),
/// matched to the project's framework, or, with no SDK on the machine, the
/// runtime this process runs on. Metadata is memory-mapped once per process.
/// </summary>
internal static class ReferenceAssemblies
{
    private static readonly ConcurrentDictionary<string, MetadataReference> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lazy<List<string>> DotnetRoots = new(FindDotnetRoots);

    public static MetadataReference? Get(string path)
    {
        try { return Cache.GetOrAdd(path, p => MetadataReference.CreateFromFile(p)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException) { return null; }
    }

    /// <summary>Framework reference paths for <paramref name="tfm"/> (e.g. net9.0, net10.0-windows) and its framework references.</summary>
    public static (List<string> Paths, string Source) Framework(string? tfm, IEnumerable<string> frameworkReferences)
    {
        var (major, bare) = ParseTfm(tfm);
        var paths = new List<string>();
        var source = "runtime";
        if (major != null)
        {
            var packs = new List<string> { "Microsoft.NETCore.App.Ref" };
            foreach (var fr in frameworkReferences)
            {
                if (fr.Equals("Microsoft.NETCore.App", StringComparison.OrdinalIgnoreCase)) continue;
                // Microsoft.WindowsDesktop.App.WPF / .WindowsForms ride in the one desktop pack.
                var name = fr.StartsWith("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase) ? "Microsoft.WindowsDesktop.App.Ref" : fr + ".Ref";
                if (!packs.Contains(name, StringComparer.OrdinalIgnoreCase)) packs.Add(name);
            }
            foreach (var pack in packs)
                if (PackDir(pack, major.Value, bare!) is { } dir)
                {
                    paths.AddRange(Directory.EnumerateFiles(dir, "*.dll"));
                    source = "targeting-pack";
                }
        }
        if (paths.Count == 0)
        {
            // No SDK here (a self-contained BrainX on a machine without .NET):
            // the runtime's own assemblies bind the same public surface.
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
            paths.AddRange(tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => Path.GetFileName(p).StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(p).StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(p).Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(p).Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase))
                .Where(p => !Path.GetFileName(p).StartsWith("Microsoft.CodeAnalysis", StringComparison.OrdinalIgnoreCase)));
            source = "runtime";
        }
        return (paths, source);
    }

    /// <summary>The C# preprocessor symbols the SDK defines for a framework, plus DEBUG and TRACE.</summary>
    public static List<string> PreprocessorSymbols(string? tfm)
    {
        var symbols = new List<string> { "DEBUG", "TRACE" };
        var (major, _) = ParseTfm(tfm);
        if (major != null)
        {
            symbols.AddRange(["NET", "NETCOREAPP", $"NET{major}_0"]);
            for (var v = 5; v <= major; v++) symbols.Add($"NET{v}_0_OR_GREATER");
            symbols.AddRange(["NETCOREAPP1_0_OR_GREATER", "NETCOREAPP2_0_OR_GREATER", "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER"]);
            if (tfm!.Contains("-windows", StringComparison.OrdinalIgnoreCase)) symbols.AddRange(["WINDOWS", "WINDOWS7_0_OR_GREATER"]);
        }
        else if (tfm?.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) == true)
            symbols.AddRange(["NETSTANDARD", tfm.ToUpperInvariant().Replace('.', '_')]);
        return symbols;
    }

    private static (int? Major, string? Bare) ParseTfm(string? tfm)
    {
        if (string.IsNullOrEmpty(tfm) || !tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase) || tfm.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase)
            || tfm.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
            return (null, null);
        var bare = tfm.Split('-')[0];
        var digits = bare[3..].Split('.')[0];
        return int.TryParse(digits, out var m) && m >= 5 ? (m, bare) : (null, null);
    }

    private static string? PackDir(string pack, int major, string bareTfm)
    {
        foreach (var root in DotnetRoots.Value)
        {
            var dir = Path.Combine(root, "packs", pack);
            if (!Directory.Exists(dir)) continue;
            var versions = Directory.EnumerateDirectories(dir)
                .Select(d => (Dir: d, Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
                .Where(x => x.Version != null && x.Version.Major == major)
                .OrderByDescending(x => x.Version)
                .ThenBy(x => Path.GetFileName(x.Dir).Contains('-'));          // a release over a preview of the same number
            foreach (var (d, _) in versions)
            {
                var refDir = Path.Combine(d, "ref", bareTfm);
                if (Directory.Exists(refDir)) return refDir;
            }
        }
        return null;
    }

    private static List<string> FindDotnetRoots()
    {
        var roots = new List<string>();
        void Add(string? p) { if (!string.IsNullOrEmpty(p) && Directory.Exists(Path.Combine(p, "packs")) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase)) roots.Add(p); }
        Add(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        // shared/Microsoft.NETCore.App/<version>/ → the root, when this process is framework-dependent.
        try { Add(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."))); } catch { }
        if (OperatingSystem.IsWindows())
        {
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
            Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"));
        }
        else
        {
            Add("/usr/share/dotnet"); Add("/usr/lib/dotnet"); Add("/usr/local/share/dotnet"); Add("/opt/homebrew/share/dotnet");
        }
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet"));
        return roots;
    }
}
