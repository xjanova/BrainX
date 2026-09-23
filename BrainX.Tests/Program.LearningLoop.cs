using System.Diagnostics;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// What the learning loop is allowed to learn from (2026-09-23). A search that
/// found nothing wrote no row, so the gap analyzer never saw its best signal;
/// and 2,473 benchmark questions sat in the log as "decisions", free to become
/// the recurring questions DreamPass tells the owner to write notes about.
/// </summary>
internal static partial class Program
{
    private static void RegisterLearningLoopChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("gap analyzer: a search that found nothing is the clearest gap, and it is counted once", GapAnalyzerChecks));
        checks.Add(("dream pass: a benchmark's questions are not the owner's", DreamPassMachineChecks));
        checks.Add(("brain_search end to end: every call leaves one query row, zero hits included", QueryRowEndToEnd));
    }

    private static string Row(DateTime ts, string op, string context, string node = "", string agent = "claude", int? hits = null)
    {
        var o = new JObject
        {
            ["ts"] = ts.ToString("O"), ["node_id"] = node, ["op"] = op,
            ["client"] = "mcp", ["agent"] = agent, ["context"] = context
        };
        if (hits != null) o["hits"] = hits;
        return o.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static Task GapAnalyzerChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-gaps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".obsidianx"));
        try
        {
            var t0 = DateTime.UtcNow.AddDays(-3);
            var lines = new List<string>();
            // Asked three times, found nothing each time — new-style query rows only.
            for (int i = 0; i < 3; i++)
                lines.Add(Row(t0.AddHours(i * 5), "query", "kubernetes ingress timeout", hits: 0));
            // Asked three times, well answered and read each time: not a gap.
            for (int i = 0; i < 3; i++)
            {
                var t = t0.AddHours(1 + i * 5);
                lines.Add(Row(t, "query", "session-handoff", hits: 8));
                for (int r = 0; r < 8; r++) lines.Add(Row(t.AddMilliseconds(r), "search", "session-handoff", node: $"n{r}"));
                lines.Add(Row(t.AddMinutes(1), "get_note", "", node: "n0"));
            }
            // From before query rows existed: two calls with one result each.
            for (int i = 0; i < 2; i++)
                lines.Add(Row(t0.AddHours(2 + i * 5), "search", "legacy thin topic", node: "x1"));
            File.WriteAllLines(Path.Combine(root, ".obsidianx", "access-log.ndjson"), lines);

            var report = new QueryGapAnalyzer().Analyze(root, windowDays: 14, limit: 10);
            var nothing = report.Suggestions.FirstOrDefault(s => s.Query == "kubernetes ingress timeout");
            Check("a query that found nothing three times is suggested", nothing != null,
                  string.Join(" | ", report.Suggestions.Select(s => s.Query)));
            Check("…with zero hits, and a reason that says so",
                  nothing?.AvgResults == 0 && nothing.Reason.Contains("found nothing"), nothing?.Reason);
            Check("a well-answered, well-read query is not a gap",
                  report.Suggestions.All(s => s.Query != "session-handoff"));
            Check("the old per-result rows still count for history before query rows",
                  report.Suggestions.Any(s => s.Query == "legacy thin topic" && s.SearchCount == 2));
            Check("a call with both a query row and result rows is counted once",
                  report.TotalSearches == 3 + 3 + 2, $"total {report.TotalSearches}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static Task DreamPassMachineChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-dream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".obsidianx"));
        try
        {
            var day0 = DateTime.UtcNow.Date.AddDays(-6).AddHours(9);
            var lines = new List<string>();
            // A benchmark: 300 questions in one afternoon, the same fact set asked
            // again three days later.
            foreach (var d in new[] { 0, 3, 5 })
                for (int i = 0; i < 100; i++)
                    lines.Add(Row(day0.AddDays(d).AddSeconds(i * 20), "recall", $"MISS conf=0.1 · benchmark fact number {i}"));
            // The owner (through an agent) asking one thing on three separate days.
            foreach (var d in new[] { 1, 2, 4 })
                lines.Add(Row(day0.AddDays(d).AddHours(3), "recall", "MISS conf=0.2 · how do we rotate the demo signing key"));
            File.WriteAllLines(Path.Combine(root, ".obsidianx", "access-log.ndjson"), lines);

            var report = new DreamPass().Analyze(root, Array.Empty<DreamPass.KnowledgeNodeLite>());
            var subjects = report.Proposals.Select(p => p.Subject).ToList();
            Check("a question asked on three days is a recurring gap",
                  report.Proposals.Any(p => p.Kind == "recurring-gap" && p.Subject.Contains("signing key")), string.Join(" | ", subjects));
            Check("a benchmark's 100-a-day questions are not", !subjects.Any(s => s.Contains("benchmark fact")), string.Join(" | ", subjects.Take(5)));
            Check("…and they are counted as set aside, not silently dropped", report.MachineQuestionRows == 300, report.MachineQuestionRows.ToString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static async Task QueryRowEndToEnd()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-query-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        var export = new BrainExport { VaultPath = vault, GeneratedAt = DateTime.UtcNow };
        var n = new NodeSummary { Id = "q00000000001", Title = "Card planner", RelativePath = "Notes/Card planner.md", ModifiedAt = DateTime.UtcNow };
        export.Nodes.Add(n);
        File.WriteAllText(Path.Combine(vault, n.RelativePath), "# Card planner\n\nKeeps HUD cards apart.");
        export.TotalNotes = 1;
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(export));

        Process? server = null;
        try
        {
            server = await StartServer(exe, vault);
            await Rpc(server, 10, "tools/call", new JObject { ["name"] = "brain_search", ["arguments"] = new JObject { ["query"] = "zebra quantum lattice" } });
            await Rpc(server, 11, "tools/call", new JObject { ["name"] = "brain_search", ["arguments"] = new JObject { ["query"] = "planner" } });
            var rows = File.ReadAllLines(Path.Combine(vault, ".obsidianx", "access-log.ndjson"))
                           .Select(l => { try { return JObject.Parse(l); } catch { return null; } })
                           .Where(o => o != null).Cast<JObject>().ToList();
            var none = rows.FirstOrDefault(o => o["op"]?.ToString() == "query" && o["context"]?.ToString() == "zebra quantum lattice");
            Check("a search that found nothing leaves a query row", none != null, string.Join("\n", rows.Select(r => r.ToString(Newtonsoft.Json.Formatting.None))));
            Check("…saying it found nothing, and which tool asked", none?["hits"]?.Value<int>() == 0 && none["tool"]?.ToString() == "brain_search");
            var some = rows.FirstOrDefault(o => o["op"]?.ToString() == "query" && o["context"]?.ToString() == "planner");
            Check("a search that found something leaves exactly one query row beside its result rows",
                  some?["hits"]?.Value<int>() == 1 && rows.Count(o => o["op"]?.ToString() == "query" && o["context"]?.ToString() == "planner") == 1
                  && rows.Any(o => o["op"]?.ToString() == "search" && o["context"]?.ToString() == "planner"));
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
