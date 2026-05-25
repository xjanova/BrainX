using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Estimates how many tokens the brain has saved Claude across its
/// lifetime by reading <c>.obsidianx/access-log.ndjson</c> — the same
/// log the MCP server writes on every tool call.
///
/// We count tool calls by op and apply a heuristic per op:
///
///   <c>brain_search</c>      → avoided ~5,000 tok of grep + read fan-out
///   <c>get_note</c>          → avoided ~3,000 tok of "open file → scan"
///   <c>brain_semantic_search</c> → ~5,500 tok (broader recall than keyword)
///   <c>brain_synthesize</c>  → ~8,000 tok (it bundles multiple notes)
///   <c>brain_get_backlinks</c> → ~2,500 tok (vs walking edge list manually)
///   <c>brain_create_note</c> → +7,500 tok of FUTURE re-derivation avoided
///   <c>brain_append_note</c> → +1,500 tok of future avoidance
///   <c>brain_remember</c>    → +500 tok of future avoidance (small note)
///
/// And subtracts the cost of running each call (~500 tok per round-trip
/// for the tool input + JSON output the model has to read).
///
/// All numbers are deliberate over-estimates of "what brain replaced",
/// not measurements — the brain has no way to know what Claude WOULD
/// have done without it. Reasonable defaults chosen from a spot survey
/// of typical "find function in repo" / "read recent doc" interactions.
/// </summary>
public class TokenSavingsTracker
{
    public int CallCost { get; set; } = 500;

    public int Estimate(string op) => op switch
    {
        "search" or "brain_search" => 5_000,
        "get_note"                 => 3_000,
        "brain_semantic_search" or "semantic_search" => 5_500,
        "brain_synthesize"  or "synthesize"   => 8_000,
        "brain_get_backlinks" or "get_backlinks" => 2_500,
        "brain_create_note" or "create_note" => 7_500,
        "brain_append_note" or "append_note" => 1_500,
        "brain_remember"    or "remember"    => 500,
        "brain_suggest_links" or "suggest_links" => 3_500,
        "brain_find_contradictions" or "find_contradictions" => 4_000,
        "brain_list" or "list"                => 1_500,
        "brain_stats" or "stats"              => 800,
        "brain_expertise" or "expertise"      => 1_200,
        "brain_import_path" or "import_path"  => 2_000,
        _ => 0
    };

    public Stats Compute(string vaultPath)
    {
        var path = Path.Combine(vaultPath, ".obsidianx", "access-log.ndjson");
        var s = new Stats();
        if (!File.Exists(path)) return s;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject obj;
                try { obj = JObject.Parse(line); } catch { continue; }
                var op = obj["op"]?.ToString() ?? "";
                var avoided = Estimate(op);
                if (avoided == 0) continue;
                s.CallsByOp[op] = s.CallsByOp.GetValueOrDefault(op) + 1;
                s.TotalCalls++;
                s.GrossSaved += avoided;
                s.GrossSpent += CallCost;
            }
        }
        catch { /* best-effort: a corrupt line stops the count there, doesn't crash */ }
        s.NetSaved = s.GrossSaved - s.GrossSpent;
        return s;
    }

    public class Stats
    {
        public int TotalCalls { get; set; }
        public Dictionary<string, int> CallsByOp { get; set; } = new();
        /// <summary>Gross tokens saved before subtracting call overhead.</summary>
        public long GrossSaved { get; set; }
        /// <summary>Tokens spent on the brain calls themselves.</summary>
        public long GrossSpent { get; set; }
        /// <summary>Net = saved - spent. Can go negative briefly on a fresh brain
        /// before any retrieval happens, but compounds positive over use.</summary>
        public long NetSaved { get; set; }
    }
}
