using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BrainX.Core.Models;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// The embedding pipeline's failure shapes (2026-09-23). From 09-16 to 09-23
/// bge-m3 was missing from Ollama: /api/tags answered, every embed 404'd, the
/// pass reported "0 written" and nothing anywhere said the vault had stopped
/// getting vectors. Each check here is one of the ways that silence happened.
/// A fake daemon stands in for Ollama — nothing here needs the real one, and
/// nothing loads the 2.3 GB in-process model.
/// </summary>
internal static partial class Program
{
    private static void RegisterEmbeddingChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("embeddings: a daemon without the model is a named problem, not '0 written'", EmbeddingProblemChecks));
        checks.Add(("embeddings: every pass records what happened, and partial vectors are redone", EmbeddingPassChecks));
        checks.Add(("embeddings: the in-process fallback refuses what it cannot afford — before loading anything", EmbeddingFallbackGuards));
        checks.Add(("section vectors: one split on every OS, and a snippet without the frontmatter", SectionTextChecks));
        checks.Add(("brain_stats end to end: the embeddings block names a stalled pipeline", BrainStatsEmbeddingsEndToEnd));
        checks.Add(("find_contradictions end to end: no chat model is 'unverified', never 'checked, all clear'", ContradictionsHonesty));
        checks.Add(("semantic search end to end: the owner's retrieval mode sets its defaults too", SemanticSearchFollowsMode));
        checks.Add(("section vectors: a sidecar older than its note counts until the note has lost sections", StaleSectionChecks));
        checks.Add(("brain_search end to end: a question keyword cannot cover is answered by meaning, and says so", SearchEscalationChecks));
        checks.Add(("fused ranking: a query that IS a note's title finds that note first, unless the name is common", ExactTitleChecks));
    }

    private static async Task ExactTitleChecks()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }
        var (root, vault, _) = EmbedVault(0);
        void Note(string rel, string text)
        {
            var p = Path.Combine(vault, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        // The report is a table of numbers: its vector sits far from its title,
        // while five notes ABOUT the benchmark sit right on the query.
        Note("Notes/Latency benchmark — gold-mini.md", "# Latency benchmark — gold-mini\n\n| arm | p50 |\n|---|---|\n| a | 12 |\n");
        for (int i = 0; i < 5; i++)
            Note($"Notes/Benchmark discussion {i}.md", $"# Benchmark discussion {i}\n\nThe latency benchmark and the gold mini set, discussed.\n");
        // A name four notes share is a question, not a lookup.
        for (int i = 0; i < 4; i++) Note($"Imported/p{i}/Setup guide.md", "# Setup guide\n\nsteps\n");
        var graph = new KnowledgeIndexer().IndexVault(vault);
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(
            BrainExporter.BuildExport(new BrainIdentity { Address = "t", DisplayName = "t" }, graph, vault)));
        var q = new[] { 1.0, 1, 1, 1, 1, 1, 1, 0 };
        var dir = Path.Combine(vault, ".obsidianx", "embeddings");
        foreach (var n in graph.Nodes)
        {
            var v = n.Title.StartsWith("Benchmark discussion") ? q.Select(x => (float)x).ToArray() : new float[] { 0, 0, 0, 0, 0, 0, 0, 1 };
            var bytes = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(dir, n.Id + ".bin"), bytes);
        }
        var report = graph.Nodes.Single(n => n.Title == "Latency benchmark — gold-mini").Id;

        using var ollama = new FakeOllama("bge-m3:latest") { Fixed = q };
        Process? on = null, off = null;
        try
        {
            on = await StartServer(exe, vault, new Dictionary<string, string> { ["BRAINX_OLLAMA_URL"] = ollama.Url, ["BRAINX_CANARY"] = "0" });
            off = await StartServer(exe, vault, new Dictionary<string, string>
                { ["BRAINX_OLLAMA_URL"] = ollama.Url, ["BRAINX_CANARY"] = "0", ["BRAINX_EXACT_TITLE"] = "0" });
            async Task<List<string>> Ids(Process server, int id, string query)
            {
                var r = ToolJson(await Rpc(server, id, "tools/call", new JObject
                {
                    ["name"] = "brain_semantic_search",
                    ["arguments"] = new JObject { ["query"] = query, ["bypass_cache"] = true, ["limit"] = 5 }
                }));
                return (r["results"] as JArray)?.Select(x => x["id"]?.ToString() ?? "").ToList() ?? new();
            }
            var without = await Ids(off, 10, "Latency benchmark — gold-mini");
            Check("fixture: without the guard, fusion loses the note by its own name", without.FirstOrDefault() != report, string.Join(",", without));
            var with = await Ids(on, 11, "latency benchmark gold mini");
            Check("with it, the title — typed any old way — finds that note first", with.FirstOrDefault() == report, string.Join(",", with));
            var common = await Ids(on, 12, "Setup guide");
            var offCommon = await Ids(off, 13, "Setup guide");
            Check("a name four notes share is left to fusion", common.SequenceEqual(offCommon), $"{string.Join(",", common)} vs {string.Join(",", offCommon)}");
        }
        finally
        {
            foreach (var s in new[] { on, off })
            {
                try { s?.Kill(entireProcessTree: true); } catch { }
                s?.Dispose();
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task SearchEscalationChecks()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }
        var (root, vault, _) = EmbedVault(0);
        void Note(string title, string body)
        {
            var p = Path.Combine(vault, "Notes", title + ".md");
            File.WriteAllText(p, $"# {title}\n\n{body}\n");
        }
        Note("Deploy checklist", "Build it, push it, restart the service. The deploy checklist.");
        Note("Rollback playbook", "When a release goes wrong: pin the previous artifact and redeploy it.");
        Note("Quarterly planning", "Themes and owners for the quarter.");
        var graph = new KnowledgeIndexer().IndexVault(vault);
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(
            BrainExporter.BuildExport(new BrainIdentity { Address = "t", DisplayName = "t" }, graph, vault)));
        // The query vector is q; only the rollback playbook points that way.
        var q = new[] { 1.0, 1, 1, 1, 1, 1, 1, 0 };
        var dir = Path.Combine(vault, ".obsidianx", "embeddings");
        string Id(string title) => graph.Nodes.Single(n => n.Title == title).Id;
        foreach (var n in graph.Nodes)
        {
            var v = n.Title == "Rollback playbook" ? q.Select(x => (float)x).ToArray() : new float[] { 0, 0, 0, 0, 0, 0, 0, 1 };
            var bytes = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(dir, n.Id + ".bin"), bytes);
        }

        using var ollama = new FakeOllama("bge-m3:latest") { Fixed = q };
        Process? on = null, off = null;
        try
        {
            on = await StartServer(exe, vault, new Dictionary<string, string> { ["BRAINX_OLLAMA_URL"] = ollama.Url, ["BRAINX_CANARY"] = "0" });
            off = await StartServer(exe, vault, new Dictionary<string, string>
                { ["BRAINX_OLLAMA_URL"] = ollama.Url, ["BRAINX_CANARY"] = "0", ["BRAINX_SEARCH_ESCALATE"] = "0" });
            async Task<JObject> Search(Process server, int id, string query) => ToolJson(await Rpc(server, id, "tools/call", new JObject
                { ["name"] = "brain_search", ["arguments"] = new JObject { ["query"] = query, ["bypass_cache"] = true } }));
            static string? First(JObject r) => (r["results"] as JArray)?.FirstOrDefault()?["id"]?.ToString();

            // "release" is in the playbook, so keyword finds it — but holds one
            // word of four: the question is about something else.
            var meaning = await Search(on, 10, "how do we undo a broken release");
            Check("a question the best keyword match barely covers is answered by meaning",
                  meaning["mode"]?.ToString() == "escalated" && First(meaning) == Id("Rollback playbook"), meaning.ToString());
            Check("…and the reply says why", meaning["escalated"]?.ToString().Contains("of the query's words") == true, meaning["escalated"]?.ToString());

            var exact = await Search(on, 11, "deploy checklist");
            Check("a question keyword covers stays keyword — no embed, no mode flag",
                  exact["mode"] == null && First(exact) == Id("Deploy checklist"), exact.ToString());

            var kept = await Search(off, 12, "how do we undo a broken release");
            Check("BRAINX_SEARCH_ESCALATE=0 keeps brain_search keyword-only", kept["mode"] == null, kept.ToString());
        }
        finally
        {
            foreach (var s in new[] { on, off })
            {
                try { s?.Kill(entireProcessTree: true); } catch { }
                s?.Dispose();
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task StaleSectionChecks()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }
        var (root, vault, _) = EmbedVault(0);
        // Sections of 200+ characters — shorter ones merge into the one before.
        // Split gives: 0 the title line, 1 First, 2 Second, 3 Third.
        string Para(string s) => string.Join(" ", Enumerable.Repeat(s, 20));
        var body = $"\n\n## First part\n\n{Para("Opening words.")}\n\n## Second part\n\n{Para("The payments rollout itself.")}"
                 + $"\n\n## Third part\n\n{Para("Closing words.")}\n";
        var knowledgeFile = Path.Combine(vault, "Notes", "Rollout plan for payments.md");
        var sessionFile = Path.Combine(vault, "Notes", "Claude-Sessions", "Session 2026-09-20 payments rollout.md");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionFile)!);
        File.WriteAllText(knowledgeFile, "# Rollout plan for payments" + body);
        File.WriteAllText(sessionFile, "# Session 2026-09-20 payments rollout" + body);
        var graph = new KnowledgeIndexer().IndexVault(vault);
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(
            BrainExporter.BuildExport(new BrainIdentity { Address = "t", DisplayName = "t" }, graph, vault)));

        // The query vector is q. Each note's whole vector points elsewhere;
        // one of its sections IS q — so only a believed section can rank it.
        var q = new[] { 1.0, 1, 1, 1, 1, 1, 1, 0 };
        var elsewhere = new float[] { 0, 0, 0, 0, 0, 0, 0, 1 };
        var qf = q.Select(x => (float)x).ToArray();
        var dir = Path.Combine(vault, ".obsidianx", "embeddings");
        string SectionsOf(string id) => SectionEmbeddings.SidecarPath(vault, id);
        foreach (var n in graph.Nodes)
        {
            var bytes = new byte[elsewhere.Length * 4];
            Buffer.BlockCopy(elsewhere, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(dir, n.Id + ".bin"), bytes);
            var split = SectionEmbeddings.Split(n.Title, File.ReadAllText(n.FilePath));
            Check($"fixture: '{n.Title}' splits into the four sections the vectors assume", split.Count == 4, $"{split.Count}");
            SectionEmbeddings.Write(SectionsOf(n.Id), [elsewhere, elsewhere, qf, elsewhere]);
            // Written a day before the note's last edit.
            File.SetLastWriteTimeUtc(SectionsOf(n.Id), n.ModifiedAt.AddDays(-1));
            File.SetLastWriteTimeUtc(Path.Combine(dir, n.Id + ".bin"), n.ModifiedAt.AddDays(-1));
        }
        var knowledge = graph.Nodes.Single(n => n.FilePath == knowledgeFile).Id;
        var session = graph.Nodes.Single(n => n.FilePath == sessionFile).Id;

        using var ollama = new FakeOllama("bge-m3:latest") { Fixed = q };
        Process? server = null;
        try
        {
            server = await StartServer(exe, vault, new Dictionary<string, string> { ["BRAINX_OLLAMA_URL"] = ollama.Url, ["BRAINX_CANARY"] = "0" });
            async Task<JObject?> Hit(int id, string noteId)
            {
                var r = ToolJson(await Rpc(server!, id, "tools/call", new JObject
                {
                    ["name"] = "brain_semantic_search",
                    ["arguments"] = new JObject { ["query"] = "payments rollout", ["compact"] = false, ["bypass_cache"] = true, ["limit"] = 10 }
                }));
                return (r["results"] as JArray)?.OfType<JObject>().FirstOrDefault(o => o["id"]?.ToString() == noteId);
            }
            // Both sidecars predate their notes; neither note has changed shape.
            var sessionHit = await Hit(10, session);
            Check("a sidecar older than its note still counts while the note splits the same way",
                  sessionHit?["section"]?.ToString() == "## Second part"
                  && (await Hit(11, knowledge))?["section"]?.ToString() == "## Second part",
                  sessionHit?.ToString() ?? "(not in the results)");

            // Both grow at the end, the way brain_append_note grows a note: their
            // first sections are still the ones the vectors were made from.
            File.AppendAllText(sessionFile, $"\n## Fourth part\n\n{Para("Appended later in the day.")}\n");
            File.AppendAllText(knowledgeFile, $"\n## Fourth part\n\n{Para("A follow-up, appended.")}\n");
            JObject? grownSession = null, grownKnowledge = null;
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(300);
                grownSession = await Hit(20 + i * 2, session);
                grownKnowledge = await Hit(21 + i * 2, knowledge);
            }
            Check("notes that only grew at the end keep their sections",
                  grownSession?["section"]?.ToString() == "## Second part" && grownKnowledge?["section"]?.ToString() == "## Second part",
                  $"{grownSession} || {grownKnowledge}");

            // The knowledge note loses a section: index 2 would now name the
            // wrong passage.
            File.WriteAllText(knowledgeFile, "# Rollout plan for payments"
                + $"\n\n## First part\n\n{Para("Opening words.")}\n\n## Third part\n\n{Para("Closing words.")}\n");
            JObject? restructured = null;
            for (int i = 0; i < 20; i++)
            {
                restructured = await Hit(50 + i, knowledge);
                if (restructured?["section"] == null) break;
                await Task.Delay(300);
            }
            Check("a note that has lost sections is ranked by its whole-note vector instead",
                  restructured?["section"] == null, restructured?.ToString() ?? "(not in the results)");
            File.SetLastWriteTimeUtc(SectionsOf(knowledge), DateTime.UtcNow.AddMinutes(1));
            File.WriteAllText(knowledgeFile, "# Rollout plan for payments" + body);
            File.SetLastWriteTimeUtc(knowledgeFile, DateTime.UtcNow);
            JObject? recomputed = null;
            for (int i = 0; i < 20; i++)
            {
                recomputed = await Hit(70 + i, knowledge);
                if (recomputed?["section"] != null) break;
                await Task.Delay(300);
            }
            Check("…and a sidecar newer than its note is always believed", recomputed?["section"]?.ToString() == "## Second part",
                  recomputed?.ToString() ?? "(not in the results)");
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task SemanticSearchFollowsMode()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-mode-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        var export = new BrainExport { VaultPath = vault, GeneratedAt = DateTime.UtcNow };
        for (int i = 0; i < 12; i++)
        {
            var n = new NodeSummary
            {
                Id = $"m{i:D11}", Title = $"Planner note {i}", RelativePath = $"Notes/Planner note {i}.md",
                ModifiedAt = DateTime.UtcNow.AddDays(-i), Preview = $"planner preview {i}"
            };
            export.Nodes.Add(n);
            File.WriteAllText(Path.Combine(vault, n.RelativePath), $"# Planner note {i}\n\nThe planner keeps cards apart ({i}).");
        }
        export.TotalNotes = export.Nodes.Count;
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(export));
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-config.json"), """{"retrievalMode":"economy"}""");

        // No embedding backend at all: Ollama at a dead port, and the
        // in-process fallback switched off. The query goes down the keyword
        // path, which is where a limit and compact still have to hold.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Process? server = null;
        try
        {
            server = await StartServer(exe, vault, new Dictionary<string, string>
            {
                ["BRAINX_OLLAMA_URL"] = $"http://127.0.0.1:{deadPort}",
                ["BRAINX_EMBED_BACKEND"] = "ollama",
            });
            async Task<JObject> Search(int id, JObject args) =>
                ToolJson(await Rpc(server!, id, "tools/call", new JObject { ["name"] = "brain_semantic_search", ["arguments"] = args }));

            var eco = await Search(10, new JObject { ["query"] = "planner cards", ["bypass_cache"] = true });
            var results = eco["results"] as JArray ?? new JArray();
            Check("economy mode: semantic search returns economy's 5, not a hard-coded 10", results.Count == 5, eco.ToString());
            Check("…compact: no preview packaging", results.Count > 0 && results.All(r => r["preview"] == null), results.FirstOrDefault()?.ToString());
            var asked = await Search(11, new JObject { ["query"] = "planner cards", ["limit"] = 7, ["compact"] = false, ["bypass_cache"] = true });
            var askedResults = asked["results"] as JArray ?? new JArray();
            Check("an explicit limit still wins", askedResults.Count == 7, asked.ToString());
            Check("…and so does compact:false", askedResults.Count > 0 && askedResults.All(r => r["preview"] != null), askedResults.FirstOrDefault()?.ToString());
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task EmbeddingProblemChecks()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var down = await new EmbeddingService { OllamaUrl = $"http://127.0.0.1:{deadPort}" }.OllamaProblemAsync("bge-m3");
        Check("no daemon → 'not reachable'", down?.StartsWith("Ollama is not reachable", StringComparison.Ordinal) == true, down);

        using (var noModel = new FakeOllama("nomic-embed-text:latest", "gemma3:4b"))
        {
            var missing = await new EmbeddingService { OllamaUrl = noModel.Url }.OllamaProblemAsync("bge-m3");
            Check("daemon up, model missing → says so", missing?.Contains("not installed") == true, missing);
            Check("…and names the command that fixes it", missing?.Contains("ollama pull bge-m3") == true, missing);
        }

        using (var ok = new FakeOllama("bge-m3:latest"))
        {
            var none = await new EmbeddingService { OllamaUrl = ok.Url }.OllamaProblemAsync("bge-m3");
            Check("daemon up with the model (as bge-m3:latest) → no problem", none == null, none);
            var chat = await new EmbeddingService { OllamaUrl = ok.Url }.OllamaProblemAsync("gemma3:4b", default, "chat model");
            Check("the same check names a missing CHAT model as one", chat?.Contains("chat model 'gemma3:4b'") == true, chat);
        }
    }

    private static async Task ContradictionsHonesty()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-contra-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var dir = Path.Combine(vault, ".obsidianx", "embeddings");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        // Twelve notes whose vectors are near each other but not duplicates,
        // so phase 1 has candidate pairs in the 0.55–0.92 band to hand over.
        var export = new BrainExport { VaultPath = vault, GeneratedAt = DateTime.UtcNow };
        var rng = new Random(7);
        // A shared direction plus independent noise: expected cosine between
        // two notes is 1 / (1 + 0.8²) ≈ 0.61, inside phase 1's 0.55–0.92 band.
        var shared = Enumerable.Range(0, 64).Select(_ => rng.NextDouble() * 2 - 1).ToArray();
        for (int i = 0; i < 12; i++)
        {
            var n = new NodeSummary { Id = $"c{i:D11}", Title = $"Claim {i}", RelativePath = $"Notes/Claim {i}.md", ModifiedAt = DateTime.UtcNow.AddDays(-1) };
            export.Nodes.Add(n);
            File.WriteAllText(Path.Combine(vault, n.RelativePath), $"# Claim {i}\n\nThe cache TTL is {i} minutes.");
            var v = shared.Select(s => (float)(s + 0.8 * (rng.NextDouble() * 2 - 1))).ToArray();
            var bytes = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(dir, n.Id + ".bin"), bytes);
        }
        export.TotalNotes = export.Nodes.Count;
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(export));

        Process? server = null;
        try
        {
            server = await StartServer(exe, vault);
            // A model no Ollama has. With the daemon up it is "not installed";
            // without one (CI) it is "not reachable". Either way nothing is
            // checked, and the answer has to say so.
            var r = ToolJson(await Rpc(server, 10, "tools/call", new JObject
            {
                ["name"] = "brain_find_contradictions",
                ["arguments"] = new JObject { ["model"] = "brainx-test-no-such-model:1b", ["limit"] = 5 }
            }));
            Check("missing chat model: mode is 'unverified', not 'llm-verified'", r["mode"]?.ToString() == "unverified", r.ToString());
            Check("…with the reason", r["problem"]?.ToString().Contains("brainx-test-no-such-model") == true, r["problem"]?.ToString());
            Check("…and the candidates still returned, labelled as unchecked",
                  (r["pairs"] as JArray)?.Count > 0 && r["note"]?.ToString().Contains("NOTHING below was checked") == true, r.ToString());
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task EmbeddingPassChecks()
    {
        var saved = Environment.GetEnvironmentVariable("BRAINX_EMBED_MODEL");
        Environment.SetEnvironmentVariable("BRAINX_EMBED_MODEL", null);
        var (root, vault, nodes) = EmbedVault(3);
        var dir = Path.Combine(vault, ".obsidianx", "embeddings");
        try
        {
            // The 09-16 outage exactly: daemon up, model gone, no fallback.
            using (var noModel = new FakeOllama("nomic-embed-text:latest"))
            {
                var svc = new EmbeddingService { OllamaUrl = noModel.Url };
                var written = await svc.PrecomputeAsync(vault, nodes);
                Check("model missing: nothing written", written == 0);
                Check("…and the pass says why", svc.BackendProblem?.Contains("not installed") == true, svc.BackendProblem);
                Check("…which is not reported as 'unreachable'", !svc.BackendUnreachable);
                Check("…and no embed was attempted into a known 404", noModel.Embeds == 0, noModel.Embeds.ToString());
                var st = EmbeddingService.ReadStatus(vault);
                Check("status.json carries the problem for brain_stats / the hook",
                      st?["problem"]?.ToString().Contains("not installed") == true, st?.ToString());
            }

            using (var ok = new FakeOllama("bge-m3:latest"))
            {
                var svc = new EmbeddingService { OllamaUrl = ok.Url };
                var written = await svc.PrecomputeAsync(vault, nodes);
                Check("healthy: one vector per note", written == 3 && nodes.All(n => File.Exists(Path.Combine(dir, n.Id + ".bin"))),
                      $"written {written}");
                Check("…at the daemon's width", new FileInfo(Path.Combine(dir, "n000.bin")).Length == FakeOllama.Dims * 4);
                var st = EmbeddingService.ReadStatus(vault);
                Check("…and status.json clears the problem", st?["problem"]?.Type == JTokenType.Null && st["written"]?.Value<int>() == 3,
                      st?.ToString());

                // A vector written in-process while the daemon was away covers
                // only the head of the note. It is fresh by mtime, and must be
                // redone anyway now that the daemon is back.
                File.WriteAllText(Path.Combine(dir, "partial.json"), new JObject { ["ids"] = new JArray("n001") }.ToString());
                var before = ok.Embeds;
                var again = await svc.PrecomputeAsync(vault, nodes);
                Check("a partial vector is re-embedded in full", again == 1 && ok.Embeds == before + 1,
                      $"written {again}, embeds {ok.Embeds - before}");
                Check("…and struck from partial.json", EmbeddingService.ReadPartial(dir).Count == 0);

                var idle = await svc.PrecomputeAsync(vault, nodes);
                Check("up to date: 0 written, and 0 is not a problem", idle == 0 && svc.BackendProblem == null, svc.BackendProblem);
            }

            // Notes edited after their vectors: every sidecar is stale.
            foreach (var n in nodes) n.ModifiedAt = DateTime.UtcNow.AddMinutes(5);
            using (var broken = new FakeOllama("bge-m3:latest") { EmbedFails = true })
            {
                var svc = new EmbeddingService { OllamaUrl = broken.Url };
                var written = await svc.PrecomputeAsync(vault, nodes);
                Check("the model listed but every embed failing: 0 written, 3 failed", written == 0 && svc.FailedCount == 3,
                      $"written {written}, failed {svc.FailedCount}");
                Check("…is a named problem, not a quiet zero", svc.BackendProblem?.Contains("failed through Ollama") == true, svc.BackendProblem);
                Check("…that status.json keeps", EmbeddingService.ReadStatus(vault)?["problem"]?.ToString().Contains("failed") == true);
            }

            using (var ok = new FakeOllama("bge-m3:latest"))
            {
                var svc = new EmbeddingService { OllamaUrl = ok.Url, MaxNotes = 1 };
                var written = await svc.PrecomputeAsync(vault, nodes);
                Check("MaxNotes=1 bounds a pass run inside a tool call", written == 1 && ok.Embeds == 1,
                      $"written {written}, embeds {ok.Embeds}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRAINX_EMBED_MODEL", saved);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task EmbeddingFallbackGuards()
    {
        var saved = Environment.GetEnvironmentVariable("BRAINX_EMBED_MODEL");
        Environment.SetEnvironmentVariable("BRAINX_EMBED_MODEL", null);
        var (rootA, vaultA, nodesA) = EmbedVault(2, complete: false);
        var (rootB, vaultB, nodesB) = EmbedVault(2, model: "nomic-embed-text");
        try
        {
            // An interrupted rebuild is pending: in-process that is hours of
            // CPU and ~11 GB. It has to say no before it loads the model.
            using (var noModel = new FakeOllama("nomic-embed-text:latest"))
            {
                var svc = new EmbeddingService { OllamaUrl = noModel.Url, AllowInProcessFallback = true };
                var written = await svc.PrecomputeAsync(vaultA, nodesA);
                Check("rebuild pending + no daemon: the fallback declines", written == 0 && svc.InProcessWritten == 0, $"written {written}");
                Check("…and says a full re-embed needs the daemon",
                      svc.BackendProblem?.Contains("full re-embed is pending") == true, svc.BackendProblem);
            }

            using (var noNomic = new FakeOllama("bge-m3:latest"))
            {
                var svc = new EmbeddingService { OllamaUrl = noNomic.Url, AllowInProcessFallback = true };
                var written = await svc.PrecomputeAsync(vaultB, nodesB);
                Check("a vault on another model: the fallback declines", written == 0, $"written {written}");
                Check("…because only bge-m3 exists in-process", svc.BackendProblem?.Contains("only runs bge-m3") == true, svc.BackendProblem);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRAINX_EMBED_MODEL", saved);
            try { Directory.Delete(rootA, recursive: true); } catch { }
            try { Directory.Delete(rootB, recursive: true); } catch { }
        }
    }

    private static Task SectionTextChecks()
    {
        var body = "---\ncreated: 2026-09-23\ntags:\n  - x\n---\n\n# My note\n\nIntro line " + new string('a', 220)
                 + "\n\n## First\n\n" + new string('b', 250)
                 + "\n\n## Second\n\n" + new string('c', 250) + "\n";
        var parts = SectionEmbeddings.Split("My note", body);
        Check("split: three sections", parts.Count == 3, parts.Count.ToString());
        // The sidecars on disk were built on Windows, where the old AppendLine
        // meant "\r\n". Line endings count toward the merge threshold, so any
        // other ending re-splits the note and points vector i at other text.
        Check("split: \"\\r\\n\" line endings on every OS, as the sidecars were built",
              parts.Count == 3 && parts[1].Contains("## First\r\n") && !parts[1].Replace("\r\n", "").Contains('\r'), parts.ElementAtOrDefault(1));

        var shown = SectionEmbeddings.Display(parts[0], "My note", 0);
        Check("display: the preamble without its frontmatter", !shown.Contains("created:") && !shown.StartsWith("---"), shown);
        Check("…without the repeated \"# title\"", !shown.StartsWith("# My note"), shown);
        Check("…starting where the words start", shown.StartsWith("Intro line"), shown);
        Check("…with \"\\n\" line endings only", !shown.Contains('\r'));
        var first = SectionEmbeddings.Display(parts[1], "My note", 1);
        Check("display: a later section keeps its heading", first.StartsWith("## First"), first);
        Check("strip: an unterminated frontmatter block is left alone",
              SectionEmbeddings.StripPreamble("---\nnot closed\nbody").StartsWith("---"));
        Check("strip: frontmatter + title and nothing else strips to empty (Display keeps the original)",
              SectionEmbeddings.StripPreamble("---\na: 1\n---\n# T") == "");

        // What the truncation audit may call unread for a note with section vectors.
        var fits = "## A\n\n" + new string('a', 3000) + "\n\n## B\n\n" + new string('b', 3000);
        Check("sections: every section inside the cap → nothing unread", SectionEmbeddings.UnreadChars(fits) == 0);
        var over = "## A\n\n" + new string('a', 5000) + "\n\n## B\n\n" + new string('b', 300);
        var overUnread = SectionEmbeddings.UnreadChars(over);
        Check("sections: only the tail past the per-section cap is unread",
              overUnread > 1000 && overUnread < 1020, overUnread.ToString());
        Check("sections: a note that does not split says so (-1), not 0",
              SectionEmbeddings.UnreadChars(new string('x', 20000)) == -1);
        return Task.CompletedTask;
    }

    private static async Task BrainStatsEmbeddingsEndToEnd()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-stats-e2e-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var dir = Path.Combine(vault, ".obsidianx", "embeddings");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        var now = DateTime.UtcNow;
        var export = new BrainExport
        {
            VaultPath = vault, GeneratedAt = now, TotalNotes = 2,
            Nodes =
            [
                new NodeSummary { Id = "aaa000000001", Title = "Old note", RelativePath = "Notes/Old note.md", ModifiedAt = now.AddDays(-9) },
                new NodeSummary { Id = "aaa000000002", Title = "New note", RelativePath = "Notes/New note.md", ModifiedAt = now.AddDays(-1) },
            ]
        };
        foreach (var n in export.Nodes)
        {
            // A snapshot taken after these files were written: the server reads
            // any note whose mtime differs as edited since, and re-reads it.
            var file = Path.Combine(vault, n.RelativePath);
            File.WriteAllText(file, $"# {n.Title}\n\nbody");
            File.SetLastWriteTimeUtc(file, n.ModifiedAt);
        }
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(export));
        var vec = Path.Combine(dir, "aaa000000001.bin");
        File.WriteAllBytes(vec, new byte[FakeOllama.Dims * 4]);
        File.SetLastWriteTimeUtc(vec, now.AddDays(-8));
        File.WriteAllText(Path.Combine(dir, "status.json"), new JObject
        {
            ["checkedAt"] = now.ToString("O"), ["model"] = "bge-m3", ["written"] = 0, ["failed"] = 0,
            ["problem"] = "Ollama is running but the embedding model 'bge-m3' is not installed — run `ollama pull bge-m3`",
        }.ToString());

        Process? server = null;
        try
        {
            var psi = new ProcessStartInfo(exe, "--serve")
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false)
            };
            psi.Environment["BRAINX_VAULT"] = vault;
            psi.Environment["BRAINX_MCP_LAUNCHER_CHILD"] = "1";
            // Throwaway vault: nothing outside it may be touched (see BRAINX_SANDBOX).
            psi.Environment["BRAINX_SANDBOX"] = "1";
            psi.Environment.Remove(StubMcpServer.EnvFlag);
            server = Process.Start(psi)!;
            server.StandardInput.AutoFlush = true;
            _ = Task.Run(async () => { try { while (await server.StandardError.ReadLineAsync() != null) { } } catch { } });

            var init = await Rpc(server, 1, "initialize", new JObject
            {
                ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "brainx-tests", ["version"] = "1" }
            });
            await server.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");

            // Claude Code shows about the first 2,000 characters of these and
            // drops the rest — the old ~10,000-char text lost its SSH, agent
            // bus and task handoff rules that way. Every rule has to land in
            // the part that is shown, even with this long temp-vault path.
            var instructions = init?["result"]?["instructions"]?.ToString() ?? "";
            var rulesEnd = instructions.IndexOf("Each tool's description carries its full protocol.", StringComparison.Ordinal);
            Check("instructions: every rule inside the ~2,000 characters Claude Code shows",
                  rulesEnd > 0 && rulesEnd < 2000, $"rules end at {rulesEnd}, total {instructions.Length}");
            Check("instructions: lead with the running version", instructions.StartsWith("BrainX MCP v", StringComparison.Ordinal));
            Check("instructions: the brain's size is read, not hard-coded", instructions.Contains("2 notes") && !instructions.Contains("600+"),
                  instructions.Length > 300 ? instructions[..300] : instructions);
            foreach (var rule in new[] { "needs_confirmation", "agentBus", "task_handoff", "unverified-owner", "#session-handoff" })
                Check($"instructions: the head carries '{rule}'", rulesEnd > 0 && instructions.IndexOf(rule, StringComparison.Ordinal) is >= 0 and var at && at < rulesEnd);

            var stats = ToolJson(await Rpc(server, 2, "tools/call", new JObject { ["name"] = "brain_stats", ["arguments"] = new JObject() }));
            var emb = stats["embeddings"] as JObject;
            Check("brain_stats has an embeddings block", emb != null, stats.ToString());
            // Every note without one is missing — the two here, and the AGENTS.md
            // the server writes at the vault root on start.
            Check("…counting what has a vector", emb?["withVector"]?.Value<int>() == 1
                  && emb["missing"]?.Value<int>() == emb["notes"]?.Value<int>() - 1 && emb["stale"]?.Value<int>() == 0, emb?.ToString());
            Check("…reporting the newest vector's age", emb?["newestVectorAt"]?.Type == JTokenType.Date, emb?.ToString());
            Check("…and warning with the cause, not just the count",
                  emb?["warning"]?.ToString().Contains("not installed") == true, emb?["warning"]?.ToString());
            Check("…with the last pass attached", emb?["lastPass"]?["problem"] != null);
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static (string Root, string Vault, List<KnowledgeNode> Nodes) EmbedVault(int notes, string model = "bge-m3", bool complete = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-embed-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var dir = Path.Combine(vault, ".obsidianx", "embeddings");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        File.WriteAllText(Path.Combine(dir, "model.json"), new JObject
        {
            ["model"] = model, ["dims"] = FakeOllama.Dims, ["maxChars"] = EmbeddingService.ResolveMaxChars(model),
            ["complete"] = complete, ["rebuildStartedAt"] = DateTime.UtcNow.AddHours(-1).ToString("O"),
            ["updatedAt"] = DateTime.UtcNow.ToString("O")
        }.ToString());
        var nodes = new List<KnowledgeNode>();
        for (int i = 0; i < notes; i++)
        {
            var path = Path.Combine(vault, "Notes", $"note{i}.md");
            File.WriteAllText(path, $"# Note {i}\n\nBody of note {i}.");
            nodes.Add(new KnowledgeNode { Id = $"n{i:D3}", Title = $"Note {i}", FilePath = path, ModifiedAt = File.GetLastWriteTimeUtc(path) });
        }
        return (root, vault, nodes);
    }

    /// <summary>
    /// Just enough of Ollama's HTTP API for a precompute pass: /api/tags,
    /// /api/ps and /api/embed. Raw TCP rather than HttpListener, which needs a
    /// URL reservation on Windows that a test run does not have. One request
    /// per connection ("Connection: close"), so there is no keep-alive to parse.
    /// </summary>
    private sealed class FakeOllama : IDisposable
    {
        public const int Dims = 8;
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string[] _models;
        private int _embeds;

        public string Url { get; }
        public bool EmbedFails { get; init; }
        /// <summary>Answer every embed with this vector — for checks that need
        /// to know exactly what a query vector will be.</summary>
        public double[]? Fixed { get; init; }
        public int Embeds => Volatile.Read(ref _embeds);

        public FakeOllama(params string[] models)
        {
            _models = models;
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(AcceptLoop);
        }

        private async Task AcceptLoop()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(() => Serve(client));
            }
        }

        private async Task Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var head = new List<byte>();
                    var one = new byte[1];
                    while (head.Count < 65536)
                    {
                        if (await stream.ReadAsync(one) == 0) return;
                        head.Add(one[0]);
                        var n = head.Count;
                        if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n') break;
                    }
                    var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n");
                    var request = lines[0].Split(' ');
                    var length = 0;
                    foreach (var l in lines)
                        if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            int.TryParse(l["Content-Length:".Length..].Trim(), out length);
                    var body = new byte[length];
                    for (var read = 0; read < length;)
                    {
                        var r = await stream.ReadAsync(body.AsMemory(read));
                        if (r == 0) break;
                        read += r;
                    }
                    var (status, json) = Answer(request[0], request.Length > 1 ? request[1] : "/", Encoding.UTF8.GetString(body));
                    var payload = Encoding.UTF8.GetBytes(json);
                    var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: application/json\r\n"
                               + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                    await stream.WriteAsync(payload);
                    await stream.FlushAsync();
                }
                catch { /* the client went away — fine for a test double */ }
            }
        }

        private (int Status, string Json) Answer(string method, string path, string body)
        {
            if (method == "GET" && path == "/api/tags")
                return (200, new JObject { ["models"] = new JArray(_models.Select(m => new JObject { ["name"] = m })) }.ToString());
            if (method == "GET" && path == "/api/ps")
                return (200, """{"models":[]}""");
            if (method == "POST" && path == "/api/embed")
            {
                Interlocked.Increment(ref _embeds);
                if (EmbedFails) return (404, """{"error":"model \"bge-m3\" not found, try pulling it first"}""");
                var input = JObject.Parse(body)["input"]?.ToString() ?? "";
                var vec = Fixed != null ? new JArray(Fixed)
                    : new JArray(Enumerable.Range(0, Dims).Select(i => (double)((input.Length + i) % 7) / 7.0 + 0.01));
                // { vec }, not (vec): new JArray(JArray) is the COPY constructor
                // and would answer a flat list instead of [[…]].
                return (200, new JObject { ["embeddings"] = new JArray { vec } }.ToString());
            }
            return (404, """{"error":"no such route"}""");
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }
}
