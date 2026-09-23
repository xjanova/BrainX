using System.Reflection;
using BrainX.Core.Models;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// What the indexer is allowed to call a tag, a category and a link
/// (2026-09-23). 34% of the vault's tags were issue numbers and colours,
/// ~40% of categories were decided by keywords found inside other words, and
/// 10,839 of 13,597 "links" were the auto-linker's guesses filed beside the
/// ones people wrote.
/// </summary>
internal static partial class Program
{
    private static void RegisterIndexerChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("indexer: tags come from prose, not from PR numbers, colours or code", IndexerTagChecks));
        checks.Add(("indexer: a category keyword counts as a word, never as part of one", IndexerCategoryChecks));
        checks.Add(("indexer: the auto-linker's guesses are kept apart from written links", IndexerLinkChecks));
        checks.Add(("brain_walk end to end: written links by default, guesses only when asked", WalkAutoLinkChecks));
    }

    private static async Task WalkAutoLinkChecks()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }
        var root = Path.Combine(Path.GetTempPath(), "brainx-walk-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        NodeSummary Note(string id, string title) => new()
        {
            Id = id, Title = title, RelativePath = $"Notes/{title}.md", ModifiedAt = DateTime.UtcNow
        };
        var lonely = Note("w00000000001", "Lonely note");
        var guessed = Note("w00000000002", "Guessed neighbour");
        var written = Note("w00000000003", "Written neighbour");
        var linked = Note("w00000000004", "Linked note");
        lonely.AutoLinkedNodeIds = [guessed.Id];
        linked.LinkedNodeIds = [written.Id];
        written.BacklinkIds = [linked.Id];
        foreach (var n in new[] { lonely, guessed, written, linked })
        {
            // The files say what the export says — the server re-reads a note
            // it finds edited since the snapshot, and would believe the file.
            var file = Path.Combine(vault, n.RelativePath);
            File.WriteAllText(file, n == linked ? $"# {n.Title}\n\nSee [[{written.Title}]].\n" : $"# {n.Title}\n");
            File.SetLastWriteTimeUtc(file, n.ModifiedAt);
        }
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(
            new BrainExport { VaultPath = vault, GeneratedAt = DateTime.UtcNow, TotalNotes = 4, Nodes = [lonely, guessed, written, linked] }));
        System.Diagnostics.Process? server = null;
        try
        {
            server = await StartServer(exe, vault);
            async Task<Newtonsoft.Json.Linq.JObject> Walk(int id, Newtonsoft.Json.Linq.JObject args) =>
                ToolJson(await Rpc(server!, id, "tools/call", new Newtonsoft.Json.Linq.JObject { ["name"] = "brain_walk", ["arguments"] = args }));
            static List<string> Ids(Newtonsoft.Json.Linq.JObject w) =>
                (w["nodes"] as Newtonsoft.Json.Linq.JArray)?.Select(n => n["id"]?.ToString() ?? "").ToList() ?? new();

            var plain = await Walk(10, new Newtonsoft.Json.Linq.JObject { ["start"] = lonely.Id, ["hops"] = 2 });
            Check("a walk from a note nobody linked stays put by default", Ids(plain).SequenceEqual(new[] { lonely.Id }), plain.ToString());
            var auto = await Walk(11, new Newtonsoft.Json.Linq.JObject { ["start"] = lonely.Id, ["hops"] = 2, ["include_auto"] = true });
            Check("include_auto walks the guess", Ids(auto).Contains(guessed.Id), auto.ToString());
            Check("…and marks the edge as a guess",
                  (auto["edges"] as Newtonsoft.Json.Linq.JArray)?.Any(e => e["auto"]?.Value<bool>() == true) == true, auto["edges"]?.ToString());
            var back = await Walk(12, new Newtonsoft.Json.Linq.JObject { ["start"] = guessed.Id, ["hops"] = 1, ["include_auto"] = true });
            Check("a guess is walked from either end", Ids(back).Contains(lonely.Id), back.ToString());
            var real = await Walk(13, new Newtonsoft.Json.Linq.JObject { ["start"] = linked.Id, ["hops"] = 1 });
            Check("written links are walked as before", Ids(real).Contains(written.Id), real.ToString());
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static KnowledgeGraph IndexTemp(Dictionary<string, string> notes, out string root, bool autoLink = true)
    {
        root = Path.Combine(Path.GetTempPath(), "brainx-idx-" + Guid.NewGuid().ToString("N"));
        foreach (var (name, text) in notes)
        {
            var p = Path.Combine(root, "Notes", name + ".md");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        var indexer = new KnowledgeIndexer();
        if (!autoLink) indexer.AutoLinker = null;
        return indexer.IndexVault(root);
    }

    private static Task IndexerTagChecks()
    {
        string? root = null;
        try
        {
            var changelog = "# CHANGELOG\n\n" + string.Join("\n", Enumerable.Range(1, 300).Select(i => $"- Merge pull request #{i} from someone/branch"));
            var graph = IndexTemp(new()
            {
                ["Tagged"] = "# Tagged\n\nA note about #brainx and #cafe culture, colour #1e1e1e and #fff.\n\n"
                           + "```csharp\n#region Setup\n#define DEBUG\n```\n\nInline `#include <x>` too.\n",
                ["CHANGELOG"] = changelog,
                ["Ordinary"] = "# Ordinary\n\n" + string.Join(" ", Enumerable.Repeat("words about the hud planner", 60)) + " #hud #planner #layout",
            }, out root, autoLink: false);
            var tagged = graph.Nodes.Single(n => n.Title == "Tagged");
            Check("real hashtags kept", tagged.Tags.Contains("brainx") && tagged.Tags.Contains("cafe"), string.Join(",", tagged.Tags));
            Check("colours dropped (#1e1e1e, #fff)", !tagged.Tags.Contains("1e1e1e") && !tagged.Tags.Contains("fff"), string.Join(",", tagged.Tags));
            Check("nothing lifted out of code (#region, #define, #include)",
                  !tagged.Tags.Any(t => t is "region" or "define" or "include"), string.Join(",", tagged.Tags));
            var log = graph.Nodes.Single(n => n.Title == "CHANGELOG");
            Check("PR numbers are not tags", !log.Tags.Any(t => t.All(char.IsDigit)), $"{log.Tags.Count} tags");
            // Its length may still count; its 300 "#123" lines may not — the
            // old formula multiplied importance by 1 + 0.1 per tag, x31 here.
            var lengthOnly = Math.Log(1 + log.WordCount);
            Check("a changelog's PR numbers no longer inflate its importance", log.Importance <= lengthOnly * 1.001,
                  $"importance {log.Importance:F2} vs length alone {lengthOnly:F2}");
            var ordinary = graph.Nodes.Single(n => n.Title == "Ordinary");
            Check("real tags still raise importance, a little", ordinary.Importance > Math.Log(1 + ordinary.WordCount),
                  $"{ordinary.Importance:F2}");
        }
        finally
        {
            if (root != null) try { Directory.Delete(root, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static Task IndexerCategoryChecks()
    {
        string? root = null;
        try
        {
            var graph = IndexTemp(new()
            {
                // Every one of these words used to score by substring:
                // ui (build, guide, quick), ux (linux), rest (interest),
                // unity (community), spa (space), roi (android), ide (guide).
                ["Linux build"] = "# Linux build\n\nWe build the guide quickly on linux. Interest from the community "
                                + "was high; the android space build is next. We build, build, build the guide again.\n",
                ["Design"] = "# Design\n\nThe UI and UX of the app: typography, layout, a colour palette in figma.\n",
                ["Bug report"] = "# Bug report\n\nSymptom: the payment page hangs. DIAGNOSIS: a health check timed out. "
                               + "อาการ: หน้าชำระเงินค้าง รักษาสถานะไม่ได้ Fix: retry the api call and deploy.\n",
                ["AI note"] = "# AI note\n\nAn AI agent answers with RAG over the notes; gpt4 and llms compared.\n",
            }, out root, autoLink: false);
            KnowledgeCategory Cat(string title) => graph.Nodes.Single(n => n.Title == title).PrimaryCategory;
            Check("a linux build note is not Design, GameDev or Web by accident",
                  Cat("Linux build") is not (KnowledgeCategory.Design_Art or KnowledgeCategory.GameDev or KnowledgeCategory.Web_Development),
                  Cat("Linux build").ToString());
            Check("real UI/UX/typography words still make it Design", Cat("Design") == KnowledgeCategory.Design_Art, Cat("Design").ToString());
            Check("a bug report's Symptom / DIAGNOSIS / อาการ do not make it medicine",
                  Cat("Bug report") != KnowledgeCategory.Health_Medicine, Cat("Bug report").ToString());
            Check("AI, RAG, gpt4 and llms count as AI words", Cat("AI note") == KnowledgeCategory.AI_MachineLearning, Cat("AI note").ToString());
        }
        finally
        {
            if (root != null) try { Directory.Delete(root, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static Task IndexerLinkChecks()
    {
        string? root = null;
        try
        {
            var graph = IndexTemp(new()
            {
                ["Alpha"] = "---\ntags: [hud, planner, layout]\n---\n# Alpha\n\nThe card planner. See [[Beta]].\n",
                ["Beta"] = "---\ntags: [hud, planner, layout]\n---\n# Beta\n\nThe card planner, again.\n",
                ["Gamma"] = "---\ntags: [hud, planner, layout]\n---\n# Gamma\n\nThe card planner, a third time.\n",
            }, out root);
            var alpha = graph.Nodes.Single(n => n.Title == "Alpha");
            var beta = graph.Nodes.Single(n => n.Title == "Beta");
            var gamma = graph.Nodes.Single(n => n.Title == "Gamma");
            Check("a written [[link]] is a link", alpha.LinkedNodeIds.Contains(beta.Id));
            Check("…and its target lists it as a backlink", beta.BacklinkIds.Contains(alpha.Id));
            var guess = graph.Edges.FirstOrDefault(e => e.RelationType.StartsWith("auto") && (e.SourceId == gamma.Id || e.TargetId == gamma.Id));
            Check("the auto-linker still relates notes that share every tag", guess != null);
            if (guess != null)
            {
                // Recorded on the edge's source, as before — only no longer as a link.
                var from = graph.Nodes.Single(n => n.Id == guess.SourceId);
                var to = graph.Nodes.Single(n => n.Id == guess.TargetId);
                Check("…but files the guess apart from written links",
                      from.AutoLinkedNodeIds.Contains(to.Id) && !from.LinkedNodeIds.Contains(to.Id),
                      $"{from.Title}→{to.Title}: linked [{string.Join(",", from.LinkedNodeIds)}], auto [{string.Join(",", from.AutoLinkedNodeIds)}]");
                Check("…and a guess is never a backlink", !to.BacklinkIds.Contains(from.Id));
            }
            Check("Gamma, which nobody linked to, has no backlinks", gamma.BacklinkIds.Count == 0, string.Join(",", gamma.BacklinkIds));

            // Thai words must survive tokenising whole: the combining vowel and
            // tone marks are Unicode marks, and splitting on them turned การ์ด
            // and การ์ตูน into a shared "word" การ.
            var tokens = typeof(AutoLinker).GetMethod("SignificantTitleTokens", BindingFlags.NonPublic | BindingFlags.Static);
            var a = ((IEnumerable<string>)tokens!.Invoke(null, ["การ์ด HUD"])!).ToList();
            var b = ((IEnumerable<string>)tokens.Invoke(null, ["การ์ตูน"])!).ToList();
            Check("Thai title words tokenise whole (การ์ด, การ์ตูน)", a.Contains("การ์ด") && b.Contains("การ์ตูน"), string.Join("|", a.Concat(b)));
            Check("…so two unrelated Thai titles share no token", !a.Intersect(b).Any(), string.Join("|", a.Intersect(b)));
        }
        finally
        {
            if (root != null) try { Directory.Delete(root, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }
}
