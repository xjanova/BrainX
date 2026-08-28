using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

/// <summary>
/// `brainx-mcp context --query "..."` — the retrieval the CHAT should have
/// been using all along, exposed for callers that are not MCP clients.
///
/// WHY THIS EXISTS. AiHubService.BuildBrainContext scores notes by counting
/// query terms in Title + Preview + Tags. It never reads a note BODY, it has
/// no embeddings, no section vectors and no fusion — and it then hands the
/// model a ~280-character preview rather than the passage that matched. On
/// this vault's own gold set that is the difference between hit@5 8.7%
/// (keyword-only) and 54.4% (shipped hybrid): the chat was running on the
/// worst retrieval in the building while the good one sat in the same repo.
///
/// This command runs the SAME HybridRank the brain_semantic_search tool
/// runs — hybrid RRF, section vectors, supersession demotion — and returns
/// the winning SECTION of each hit rather than its opening paragraph, which
/// is what the 2026-08-13 brain-on/off benchmark showed was the difference
/// between "retrieval worked" and "the model said the notes do not contain
/// that information".
/// </summary>
internal static partial class Program
{
    internal static int ContextCli(string[] args)
    {
        string? query = null, vaultArg = null, scope = null;
        int limit = 6, chars = 1400;
        var asJson = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--query" or "-q" when i + 1 < args.Length: query = args[++i]; break;
                case "--file" when i + 1 < args.Length: query = File.ReadAllText(args[++i]); break;
                case "--vault" when i + 1 < args.Length: vaultArg = args[++i]; break;
                case "--scope" when i + 1 < args.Length: scope = args[++i]; break;
                case "--limit" when i + 1 < args.Length: int.TryParse(args[++i], out limit); break;
                case "--chars" when i + 1 < args.Length: int.TryParse(args[++i], out chars); break;
                case "--json": asJson = true; break;
                case "-h" or "--help" or "help":
                    Console.WriteLine("Usage: brainx-mcp context --query TEXT [--vault PATH] [--scope S]");
                    Console.WriteLine("                         [--limit N] [--chars N] [--json]");
                    Console.WriteLine();
                    Console.WriteLine("Retrieves brain context for a question using the SAME hybrid ranking");
                    Console.WriteLine("brain_semantic_search uses, and returns the matching SECTION of each");
                    Console.WriteLine("note rather than its opening paragraph. Printed as markdown by");
                    Console.WriteLine("default, ready to paste into a system prompt.");
                    return 0;
            }
        }
        if (string.IsNullOrWhiteSpace(query))
        {
            Console.Error.WriteLine("--query is required");
            return 2;
        }
        if (!string.IsNullOrWhiteSpace(vaultArg) && Directory.Exists(vaultArg))
            _vaultPath = Path.GetFullPath(vaultArg);

        try
        {
            var res = BrainSemanticSearch(new JObject
            {
                ["query"] = query,
                ["limit"] = Math.Clamp(limit, 1, 20),
                ["preview_chars"] = Math.Clamp(chars, 200, 4000),
                ["compact"] = false,
                ["scope"] = scope ?? "",
                ["bypass_cache"] = true,
            }) as JObject;

            var hits = res?["results"] as JArray ?? new JArray();
            if (asJson) { Console.WriteLine((res ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None)); return 0; }

            if (hits.Count == 0)
            {
                // Say so explicitly rather than printing nothing: a caller that
                // pastes an empty string into a prompt cannot tell "the brain
                // has nothing" from "retrieval failed", and the model then
                // invents an answer to fill the silence.
                Console.WriteLine("(no matching notes in the brain for this question)");
                return 0;
            }

            var sb = new StringBuilder();
            foreach (var h in hits)
            {
                var title = h["title"]?.ToString();
                var section = h["section"]?.ToString();
                var body = h["matchContext"]?.ToString() ?? h["preview"]?.ToString() ?? "";
                var path = h["path"]?.ToString();
                var when = h["modifiedAt"]?.ToString();

                sb.AppendLine();
                sb.AppendLine($"### {title}{(string.IsNullOrEmpty(section) ? "" : "  →  " + section)}");
                var meta = new List<string>();
                if (!string.IsNullOrEmpty(path)) meta.Add(path);
                if (!string.IsNullOrEmpty(when) && when.Length >= 10) meta.Add(when[..10]);
                // Superseded notes still appear, but never silently: a fact
                // whose window has closed is the most dangerous thing this can
                // hand a model.
                if (h["superseded"]?.ToObject<bool>() == true)
                    meta.Add("SUPERSEDED — a newer note replaces this");
                if (meta.Count > 0) sb.AppendLine($"_{string.Join(" · ", meta)}_");
                sb.AppendLine();
                sb.AppendLine(body.Trim());
            }
            Console.WriteLine(sb.ToString().Trim());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"context failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
