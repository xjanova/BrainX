using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace BrainX.CodeGraph.CSharp;

/// <summary>
/// What a .csproj says that changes how its code binds: target framework,
/// implicit and explicit global usings, project references, and the package
/// and framework assemblies a restore resolved. Read from the XML and
/// obj/project.assets.json directly; MSBuild is never run, so a project that
/// was never restored still binds against the framework, only less exactly.
/// </summary>
internal sealed class CsProject
{
    public required string FullPath { get; init; }
    public string Dir => Path.GetDirectoryName(FullPath)!;
    public string Name => Path.GetFileNameWithoutExtension(FullPath);
    public string Sdk { get; private set; } = "Microsoft.NET.Sdk";
    public string? TargetFramework { get; private set; }
    public bool ImplicitUsings { get; private set; }
    public bool UseWpf { get; private set; }
    public bool UseWindowsForms { get; private set; }
    public bool AllowUnsafe { get; private set; }
    public List<string> Usings { get; } = [];
    public List<string> RemovedUsings { get; } = [];
    public List<string> ProjectReferences { get; } = [];
    /// <summary>Compile-time package assemblies from project.assets.json, full paths.</summary>
    public List<string> PackageAssemblies { get; } = [];
    /// <summary>Microsoft.AspNetCore.App, Microsoft.WindowsDesktop.App… from project.assets.json.</summary>
    public HashSet<string> FrameworkReferences { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The newest intermediate folder (obj/&lt;config&gt;/&lt;tfm&gt;) holding generated sources, if any.</summary>
    public string? IntermediateDir { get; private set; }
    public bool Restored { get; private set; }

    public static CsProject Load(string path, string repoRoot)
    {
        var p = new CsProject { FullPath = Path.GetFullPath(path) };
        var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Directory.Build.props from the repo root down, then the project: the nearer wins.
        var chain = new List<string>();
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar);
        for (var d = new DirectoryInfo(p.Dir); d != null; d = d.Parent)
        {
            var dbp = Path.Combine(d.FullName, "Directory.Build.props");
            if (File.Exists(dbp)) chain.Insert(0, dbp);
            if (string.Equals(d.FullName.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) break;
        }
        chain.Add(p.FullPath);
        foreach (var file in chain)
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException) { continue; }
            var proj = doc.Root;
            if (proj == null) continue;
            if (file == p.FullPath && proj.Attribute("Sdk")?.Value is { Length: > 0 } sdk) p.Sdk = sdk;
            foreach (var group in proj.Elements().Where(e => e.Name.LocalName == "PropertyGroup"))
                foreach (var prop in group.Elements())
                    if (prop.Attribute("Condition") == null) props[prop.Name.LocalName] = prop.Value.Trim();
            foreach (var item in proj.Elements().Where(e => e.Name.LocalName == "ItemGroup").SelectMany(g => g.Elements()))
            {
                if (item.Attribute("Condition") != null && file != p.FullPath) continue;
                var include = item.Attribute("Include")?.Value;
                var remove = item.Attribute("Remove")?.Value;
                switch (item.Name.LocalName)
                {
                    case "Using" when include != null && item.Attribute("Static") == null && item.Attribute("Alias") == null:
                        p.Usings.Add(include); break;
                    case "Using" when remove != null:
                        p.RemovedUsings.Add(remove); break;
                    case "ProjectReference" when include != null && file == p.FullPath:
                        var full = Path.GetFullPath(Path.Combine(p.Dir, include.Replace('\\', Path.DirectorySeparatorChar)));
                        if (File.Exists(full)) p.ProjectReferences.Add(full);
                        break;
                }
            }
        }
        var tfm = props.GetValueOrDefault("TargetFramework");
        if (string.IsNullOrEmpty(tfm) && props.GetValueOrDefault("TargetFrameworks") is { Length: > 0 } tfms)
            tfm = tfms.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        p.TargetFramework = string.IsNullOrEmpty(tfm) || tfm.Contains("$(") ? null : tfm;
        p.ImplicitUsings = props.GetValueOrDefault("ImplicitUsings") is "enable" or "true";
        p.UseWpf = props.GetValueOrDefault("UseWPF") is "true";
        p.UseWindowsForms = props.GetValueOrDefault("UseWindowsForms") is "true";
        p.AllowUnsafe = props.GetValueOrDefault("AllowUnsafeBlocks") is "true";
        p.ReadAssets();
        p.IntermediateDir = NewestIntermediate(Path.Combine(p.Dir, "obj"), p.TargetFramework);
        return p;
    }

    /// <summary>The SDK's implicit global usings for this project type.</summary>
    public IEnumerable<string> GlobalUsings()
    {
        var set = new List<string>();
        if (ImplicitUsings)
        {
            set.AddRange(["System", "System.Collections.Generic", "System.Linq", "System.Threading", "System.Threading.Tasks"]);
            // The desktop SDK leaves these two out: they collide with WPF and WinForms names.
            if (!UseWpf && !UseWindowsForms) set.AddRange(["System.IO", "System.Net.Http"]);
            if (Sdk.Contains("Web", StringComparison.OrdinalIgnoreCase))
                set.AddRange(["System.Net.Http.Json", "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Http",
                              "Microsoft.AspNetCore.Routing", "Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection",
                              "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging"]);
            if (Sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase))
                set.AddRange(["Microsoft.Extensions.Configuration", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging"]);
            if (UseWindowsForms) set.Add("System.Windows.Forms");
            if (UseWindowsForms) set.Add("System.Drawing");
        }
        set.AddRange(Usings);
        return set.Except(RemovedUsings, StringComparer.Ordinal).Distinct(StringComparer.Ordinal);
    }

    private void ReadAssets()
    {
        var assets = Path.Combine(Dir, "obj", "project.assets.json");
        if (!File.Exists(assets)) return;
        JObject root;
        try { root = JObject.Parse(File.ReadAllText(assets)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException) { return; }
        var folders = (root["packageFolders"] as JObject)?.Properties().Select(x => x.Name).Where(Directory.Exists).ToList() ?? [];
        var libraries = root["libraries"] as JObject;
        var targets = root["targets"] as JObject;
        if (targets == null || libraries == null) return;
        // "net9.0", or the framework's long name in older assets; never a RID-specific target.
        var target = targets.Properties().Where(t => !t.Name.Contains('/'))
            .OrderByDescending(t => TargetFramework != null && (t.Name == TargetFramework || t.Name.StartsWith(TargetFramework + "/", StringComparison.Ordinal)))
            .FirstOrDefault()?.Value as JObject;
        if (target == null) return;
        Restored = true;
        foreach (var lib in target.Properties())
        {
            if ((lib.Value["type"]?.ToString()) != "package") continue;
            var libPath = libraries[lib.Name]?["path"]?.ToString();
            if (libPath == null || lib.Value["compile"] is not JObject compile) continue;
            foreach (var asset in compile.Properties().Select(x => x.Name))
            {
                if (asset.EndsWith("_._", StringComparison.Ordinal) || !asset.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var folder in folders)
                {
                    var full = Path.Combine(folder, libPath, asset.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full)) { PackageAssemblies.Add(full); break; }
                }
            }
        }
        if (root["project"]?["frameworks"] is JObject fw)
            foreach (var f in fw.Properties())
                if (f.Value["frameworkReferences"] is JObject refs)
                    foreach (var r in refs.Properties()) FrameworkReferences.Add(r.Name);
    }

    private static string? NewestIntermediate(string obj, string? tfm)
    {
        if (!Directory.Exists(obj)) return null;
        try
        {
            return Directory.EnumerateDirectories(obj)                       // Debug, Release
                .SelectMany(cfg => Directory.EnumerateDirectories(cfg))     // net9.0, net10.0-windows
                .Where(d => tfm == null || Path.GetFileName(d).Equals(tfm, StringComparison.OrdinalIgnoreCase))
                .Where(d => Directory.EnumerateFiles(d, "*.GlobalUsings.g.cs").Any() || Directory.EnumerateFiles(d, "*.g.cs").Any())
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
