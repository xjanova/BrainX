using System.Diagnostics;
using BrainX.Core.Services;
using BrainX.Server.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

internal static partial class Program
{
    private static void RegisterSearchIndexChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("search index: symbols include both regex branches and names beyond the tag cap", CodeSymbolIndexChecks));
        checks.Add(("search index: trigram candidates retain every substring through updates and slot reuse", TrigramPostingChecks));
        checks.Add(("search index: real MCP rankings match a full scan, lookups refuse ambiguity", SearchIndexEndToEnd));
    }

    private static Task CodeSymbolIndexChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-symbols-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var examples = new[]
            {
                (".cs", "public class Rocket { public void Launch() {} }", new[] { "Rocket", "Launch" }),
                (".ts", "export function launch() {}\nexport const land = () => {};", new[] { "launch", "land" }),
                (".js", "class Rocket {}\nconst launch = () => {};", new[] { "Rocket", "launch" }),
                (".py", "class Rocket:\n    async def launch(self): pass", new[] { "Rocket", "launch" }),
                (".go", "type Rocket struct {}\nfunc Launch() {}", new[] { "Rocket", "Launch" }),
                (".rs", "pub struct Rocket {}\npub fn launch() {}", new[] { "Rocket", "launch" }),
            };
            foreach (var (ext, code, expected) in examples)
            {
                var path = Path.Combine(root, "sample" + ext);
                File.WriteAllText(path, code);
                var node = CodeIndexer.Index(path);
                Check(ext + ": declarations and methods/arrow functions both searchable", expected.All(node.Tags.Contains), string.Join(",", node.Tags));
            }
            var big = Path.Combine(root, "Many.cs");
            File.WriteAllText(big, string.Join("\n", Enumerable.Range(0, 80).Select(i => $"public class Widget{i} {{}}")));
            var many = CodeIndexer.Index(big);
            Check("all symbols survive the 40-tag display cap", many.Tags.Count == 42
                && JArray.FromObject(many.Properties["symbols"]!).Count == 80);
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    private static Task TrigramPostingChecks()
    {
        var index = new TrigramCandidateIndex();
        var random = new Random(731);
        var docs = new Dictionary<string, string>();
        const string alphabet = "abcdef ABCDEF012345กขคงจค้นหาโน้ต";
        for (int i = 0; i < 150; i++)
        {
            var text = new string(Enumerable.Range(0, 100).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            docs[i.ToString()] = text;
            index.Set(i.ToString(), [text]);
        }
        var misses = 0;
        foreach (var (id, text) in docs)
            for (int i = 0; i < 30; i++)
            {
                var needle = text.Substring(random.Next(text.Length - 7), random.Next(3, 8));
                if (index.Candidates([needle]) is { } ids && !ids.Contains(id)) misses++;
            }
        Check("4500 sampled substrings have no false negatives", misses == 0, misses.ToString());
        index.Set("0", ["unique replacement payload"]);
        Check("changed content has new postings", index.Candidates(["replacement"])?.Contains("0") == true);
        index.Remove("0");
        Check("deleted ids leave no postings", index.Candidates(["replacement"])?.Contains("0") == false);
        index.Set("new", ["replacement"]);
        Check("a reused slot resolves to its new id", index.Candidates(["replacement"])?.SetEquals(["new"]) == true);
        Check("short and other-script queries fall back to the exact scorer", index.Candidates(["AI"]) == null && index.Candidates(["κόσμος"]) == null);
        return Task.CompletedTask;
    }

    private static async Task SearchIndexEndToEnd()
    {
        var exe = FindMcpExe() ?? throw new InvalidOperationException("Build brainx-mcp first");
        var (root, vault, _) = EmbedVault(0);
        using var ollama = new FakeOllama("bge-m3:latest") { Fixed = [1, 1, 1, 1, 1, 1, 1, 0] };
        Process? indexed = null, scan = null;
        try
        {
            WriteNote(vault, "Notes/Alpha.md", "---\ntags: [metatag]\n---\n# Alpha\n\n## Headingneedle\n\n" + new string('x', 1200) + " deepneedle deploy deployment HUD ui AI การค้นหาโน้ตในสมอง café κόσμος.\n");
            WriteNote(vault, "Notes/Beta.md", "# Beta\n\nStart build interest brain video guide community.\n");
            WriteNote(vault, "Imported/a/Shared.md", "# Shared\n\nfrom a\n");
            WriteNote(vault, "Imported/b/Shared.md", "# Shared\n\nfrom b\n");
            WriteNote(vault, "Notes/Old.md", "# Old\n\nprevious procedure\n");
            WriteNote(vault, "Notes/New.md", "---\nsupersedes: ['[[Old]]']\n---\n# New\n\ncurrent procedure\n");
            WriteNote(vault, "Code/First.cs", "public class Rocket { public void Launch() {} }\n" + string.Join("\n", Enumerable.Range(0, 60).Select(i => $"public class Widget{i} {{}}")));
            WriteNote(vault, "Code/Second.cs", "public class Another { public void Launch() {} }");
            // Similarity deliberately retrieves this without any lexical overlap.
            WriteNote(vault, "Notes/Hidden.md", "# Hidden\n\nA semantic-only answer.\n");
            for (int i = 0; i < 180; i++) WriteNote(vault, $"Noise/Item{i}.md", $"# Item{i}\n\nordinary filler data row{i} {new string((char)('a' + i % 26), 200)}\n");
            var snapshot = Snapshot(vault);
            var hidden = snapshot.Nodes.Single(n => n.Title == "Hidden").Id;
            var alpha = snapshot.Nodes.Single(n => n.Title == "Alpha").Id;
            foreach (var n in snapshot.Nodes)
            {
                var v = n.Id == hidden ? new float[] { 1, 1, 1, 1, 1, 1, 1, 0 } : new float[] { 0, 0, 0, 0, 0, 0, 0, 1 };
                var bytes = new byte[v.Length * 4];
                Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
                File.WriteAllBytes(Path.Combine(vault, ".obsidianx", "embeddings", n.Id + ".bin"), bytes);
            }
            var exportPath = Path.Combine(vault, ".obsidianx", "brain-export.json");
            File.WriteAllText(exportPath, JsonConvert.SerializeObject(snapshot));
            // Keep both processes on one snapshot; otherwise one can cross the
            // overlay's 2 s rescan boundary while the other still has old metadata.
            // Bodies are still checked live; overlay semantics have their own suite.
            var env = new Dictionary<string, string> { ["BRAINX_CANARY"] = "0", ["BRAINX_OLLAMA_URL"] = ollama.Url, ["BRAINX_SEARCH_ESCALATE"] = "0", ["BRAINX_OVERLAY"] = "0" };
            indexed = await StartServer(exe, vault, env);
            scan = await StartServer(exe, vault, new Dictionary<string, string>(env) { ["BRAINX_KEYWORD_INDEX"] = "0" });
            int rpc = 10;
            async Task<JObject> Call(Process p, string tool, JObject args) => ToolJson(await Rpc(p, rpc++, "tools/call", new JObject { ["name"] = tool, ["arguments"] = args }));
            static JArray Signature(JObject result) => new((result["results"] as JArray ?? []).Select(n => new JObject { ["id"] = n["id"], ["score"] = n["score"] }));
            async Task Compare(string query, string? scope = null, string tool = "brain_search")
            {
                var args = new JObject { ["query"] = query, ["limit"] = 100, ["bypass_cache"] = true, ["scope"] = scope };
                var a = await Call(indexed!, tool, args);
                var b = await Call(scan!, tool, args);
                Check($"{tool}: same ids, order and scores for {query} ({scope ?? "all"})", JToken.DeepEquals(Signature(a), Signature(b)), $"{Signature(a)} vs {Signature(b)}");
            }
            foreach (var q in new[] { "Alpha", "deepneedle", "Headingneedle", "metatag", "Programming", "deploy", "deployment", "HUD", "ui", "AI", "id", "a", "the", "start build", "missingword", "ระบบค้นหาโน้ตทำงานอย่างไร", "การค้นหาโน้ต", "café", "κόσμος", "Launch", "Widget59", "deepneedle absent", "alpha\nheadingneedle" }) await Compare(q);
            await Compare("deepneedle", "Notes");
            await Compare("deepneedle", "not-a-scope");
            await Compare("an unrelated question", tool: "brain_semantic_search");
            var semantic = await Call(indexed, "brain_semantic_search", new JObject { ["query"] = "an unrelated question", ["bypass_cache"] = true });
            Check("semantic-only evidence survives lexical pruning", (semantic["results"] as JArray)?.Any(n => n["id"]?.ToString() == hidden) == true, semantic.ToString());

            var before = ollama.Embeds;
            async Task<JObject> Lookup(string reference, string kind = "auto", string? scope = null) => await Call(indexed, "brain_lookup", new JObject { ["reference"] = reference, ["kind"] = kind, ["scope"] = scope });
            Check("lookup: duplicate titles are ambiguous", (await Lookup("Shared", "title"))["status"]?.ToString() == "ambiguous");
            Check("lookup: a project scope resolves a duplicate", (await Lookup("Shared", "title", "Imported/a"))["status"]?.ToString() == "exact");
            Check("lookup: a wrong scope never broadens", (await Lookup("Alpha", "title", "Imported/a"))["status"]?.ToString() == "out-of-scope");
            Check("lookup: exact path normalises Windows separators", (await Lookup(".\\Notes\\Alpha.md", "path"))["results"]?[0]?["id"]?.ToString() == alpha);
            Check("lookup: id lookup is exact", (await Lookup(alpha, "id"))["status"]?.ToString() == "exact");
            Check("lookup: duplicate methods are ambiguous", (await Lookup("Launch", "symbol"))["count"]?.Value<int>() == 2);
            Check("lookup: symbols beyond the tag cap resolve", (await Lookup("Widget59", "symbol"))["status"]?.ToString() == "exact");
            Check("lookup: a retired note keeps its replacement", (await Lookup("Old", "title"))["results"]?[0]?["superseded"]?.Value<bool>() == true);
            Check("lookup: a missing name is explicit", (await Lookup("NoSuchExactName"))["status"]?.ToString() == "not-found");
            Check("lookup: no query embeddings were requested", ollama.Embeds == before, $"{before} -> {ollama.Embeds}");
            Check("lookup: remote read scope can use it", McpRemotePolicy.IsAllowed("brain_lookup", McpScope.Read));
            var listed = await Rpc(indexed, rpc++, "tools/list", new JObject());
            Check("lookup: advertised over MCP", (listed?["result"]?["tools"] as JArray)?.Any(t => t["name"]?.ToString() == "brain_lookup") == true);

            WriteNote(vault, "Notes/Alpha.md", "# Alpha\n\nreplacementneedle\n");
            File.SetLastWriteTimeUtc(Path.Combine(vault, "Notes", "Alpha.md"), DateTime.UtcNow.AddSeconds(1));
            await Compare("replacementneedle");
            await Compare("deepneedle");
            File.Delete(Path.Combine(vault, "Notes", "Alpha.md"));
            File.WriteAllText(exportPath, JsonConvert.SerializeObject(Snapshot(vault)));
            File.SetLastWriteTimeUtc(exportPath, DateTime.UtcNow.AddSeconds(2));
            await Compare("replacementneedle");
            Check("lookup: deleted notes leave the identity index", (await Lookup(alpha, "id"))["status"]?.ToString() == "not-found");
        }
        finally
        {
            foreach (var p in new[] { indexed, scan }) { try { p?.Kill(true); } catch { } p?.Dispose(); }
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
