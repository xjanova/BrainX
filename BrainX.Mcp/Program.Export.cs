using System.Diagnostics;
using BrainX.Core.Models;
using BrainX.Core.Services;
using Newtonsoft.Json;

namespace BrainX.Mcp;

/// <summary>
/// `brainx-mcp export` — re-index the vault and rewrite brain-export.json.
///
/// This existed only inside the WPF client ("Export Brain Now") and inside the
/// server, which meant a headless machine could IMPORT notes it could then
/// never SEE: `brain_import_path` writes files into the vault, every search
/// path reads brain-export.json, and nothing on the command line rebuilt it.
/// The import returned "imported: 8" and the brain went on answering as if
/// none of them existed — a half-open loop that looks like success.
///
/// Identity is read from the vault's identity.json when present and otherwise
/// synthesised as an anonymous local one. It never GENERATES and PERSISTS a
/// keypair: a signing identity created as a side effect of a re-index is the
/// kind of thing that later gets treated as the real one.
/// </summary>
internal static partial class Program
{
    internal static int ExportCli(string[] args)
    {
        string? vaultArg = null;
        var quiet = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vault" && i + 1 < args.Length) vaultArg = args[++i];
            else if (args[i] == "--quiet") quiet = true;
            else if (args[i] is "-h" or "--help" or "help")
            {
                Console.WriteLine("Usage: brainx-mcp export [--vault PATH] [--quiet]");
                Console.WriteLine();
                Console.WriteLine("Re-indexes the vault and rewrites .obsidianx/brain-export.json,");
                Console.WriteLine("which every search path reads. Run this after importing or");
                Console.WriteLine("hand-adding notes on a machine with no BrainX client.");
                return 0;
            }
        }
        if (!string.IsNullOrWhiteSpace(vaultArg) && Directory.Exists(vaultArg))
            _vaultPath = Path.GetFullPath(vaultArg);

        void Say(string s) { if (!quiet) Console.WriteLine(s); }

        Say($"brainx-mcp export · v{ServerVersion}");
        Say($"  vault: {_vaultPath}");

        var before = 0;
        try
        {
            var prev = LoadExport();
            before = prev?.Nodes.Count ?? 0;
        }
        catch { }

        var sw = Stopwatch.StartNew();
        var graph = new KnowledgeIndexer().IndexVault(_vaultPath);
        Say($"  indexed {graph.Nodes.Count} note(s) in {sw.ElapsedMilliseconds:n0} ms");

        var identity = LoadOrAnonymousIdentity(_vaultPath);
        var result = new BrainExporter().Export(_vaultPath, identity, graph);
        sw.Stop();

        Say($"  wrote  {result.JsonPath}");
        Say($"  notes  {before} → {result.NodeCount}"
          + (result.NodeCount == before ? "" : $"  ({result.NodeCount - before:+#;-#;0})"));
        Say($"  done in {sw.Elapsed.TotalSeconds:F1}s");
        // The export is what search reads; the sidecars are what it scores.
        // A note added here has no vector until an embed pass runs, and the
        // difference is invisible in this command's output — so it says so.
        Say("  note: new notes need `brainx-mcp garden` (or embed / embed-sections) before semantic search can score them.");
        return 0;
    }

    private static BrainIdentity LoadOrAnonymousIdentity(string vaultPath)
    {
        try
        {
            var p = Path.Combine(vaultPath, ".obsidianx", "identity.json");
            if (File.Exists(p))
            {
                var id = JsonConvert.DeserializeObject<BrainIdentity>(File.ReadAllText(p));
                if (id != null && !string.IsNullOrWhiteSpace(id.Address)) return id;
            }
        }
        catch { }
        // Address, not a keypair: enough for the export header, useless for
        // signing, and impossible to mistake for a real identity.
        return new BrainIdentity
        {
            Address = "local",
            DisplayName = Path.GetFileName(vaultPath.TrimEnd('\\', '/')),
            CreatedAt = DateTime.UtcNow
        };
    }
}
