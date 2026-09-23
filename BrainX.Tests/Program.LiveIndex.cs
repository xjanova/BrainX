using System.Diagnostics;
using BrainX.Core.Models;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// What the MCP server sees between full indexes (2026-09-23). brain-export.json
/// is rebuilt only by the desktop client, so a note written through the MCP
/// could not be read back by the id it was given, found by search, or embedded,
/// until someone opened the client.
/// </summary>
internal static partial class Program
{
    private static void RegisterLiveIndexChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("live index: notes written, edited or deleted since the export read as a full re-index would", OverlayMatchesFullIndex));
        checks.Add(("live index: a folder or drive that vanishes is not read as deleted notes", OverlayVanishGuard));
        checks.Add(("embed-on-write: a vector now, the full one at the next pass, nothing mid-rebuild", EmbedNoteNowChecks));
        checks.Add(("live index end to end: create, read back by id, find from another session, embed", LiveIndexEndToEnd));
        checks.Add(("findability canary: the newest notes come back by their own titles, or brain_stats says they don't", CanaryChecks));
    }

    private static (int Code, string Output) RunCliWith(string exe, string arguments, IReadOnlyDictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = new System.Text.UTF8Encoding(false)
        };
        psi.Environment.Remove(StubMcpServer.EnvFlag);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit(120_000);
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    private static async Task CanaryChecks()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }
        var (root, vault, _) = EmbedVault(0);
        WriteNote(vault, "Notes/Ledger reconciliation walkthrough.md", "# Ledger reconciliation walkthrough\n\nMatch every entry twice.\n");
        WriteNote(vault, "Notes/Quarterly roadmap planning.md", "# Quarterly roadmap planning\n\nThree themes, one owner each.\n");
        WriteNote(vault, "Notes/Ingress timeout on the staging cluster.md", "# Ingress timeout on the staging cluster\n\nRaise proxy-read-timeout.\n");
        // Titles that cannot stand as a query: too short, shared by two notes,
        // and a rules file (which nobody asks for by name).
        WriteNote(vault, "Notes/README.md", "# README\n");
        WriteNote(vault, "Imported/a/Shared deployment checklist.md", "# one\n");
        WriteNote(vault, "Imported/b/Shared deployment checklist.md", "# two\n");
        WriteNote(vault, "V3_CODING_GUIDELINES.md", "# guidelines\n");
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(Snapshot(vault)));
        var resultPath = Path.Combine(vault, ".obsidianx", "findability.json");
        JObject Result() => JObject.Parse(File.ReadAllText(resultPath));

        using var ollama = new FakeOllama("bge-m3:latest");
        Process? server = null;
        try
        {
            var (code, output) = RunCliWith(exe, $"canary --vault \"{vault}\"", new Dictionary<string, string> { ["BRAINX_OLLAMA_URL"] = ollama.Url });
            var ok = Result();
            Check("canary: asks only for notes whose title can stand as a query — not README, a shared name, or a rules file",
                  ok["checked"]?.Value<int>() == 3, ok.ToString());
            Check("…finds each by its own title through brain_recall's ranking, with the queries embedded",
                  code == 0 && ok["found"]?.Value<int>() == 3 && ok["queriesEmbedded"]?.Value<int>() == 3 && ok["problem"]?.Type == JTokenType.Null, output);

            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var deadPort = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var (downCode, downOutput) = RunCliWith(exe, $"canary --vault \"{vault}\"", new Dictionary<string, string>
                { ["BRAINX_OLLAMA_URL"] = $"http://127.0.0.1:{deadPort}", ["BRAINX_EMBED_BACKEND"] = "ollama" });
            var down = Result();
            Check("with semantic search down, found-by-keyword is not called healthy",
                  downCode == 1 && down["problem"]?.ToString().Contains("keyword-only") == true, downOutput);

            server = await StartServer(exe, vault, new Dictionary<string, string> { ["BRAINX_CANARY"] = "0", ["BRAINX_OLLAMA_URL"] = ollama.Url });
            var stats = ToolJson(await Rpc(server, 10, "tools/call", new JObject { ["name"] = "brain_stats", ["arguments"] = new JObject() }));
            Check("brain_stats carries the last canary result, problem and notes included",
                  stats["findability"]?["problem"]?.ToString().Contains("keyword-only") == true && stats["findability"]?["notes"] is JArray { Count: 3 },
                  stats["findability"]?.ToString());
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static BrainExport Snapshot(string vault)
    {
        var graph = new KnowledgeIndexer().IndexVault(vault);
        return BrainExporter.BuildExport(new BrainIdentity { Address = "test", DisplayName = "test" }, graph, vault);
    }

    private static void WriteNote(string vault, string rel, string text)
    {
        var p = Path.Combine(vault, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
    }

    private static Task OverlayMatchesFullIndex()
    {
        var vault = Path.Combine(Path.GetTempPath(), "brainx-overlay-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteNote(vault, "Notes/Alpha.md", "# Alpha\n\nThe first note. #brainx\n");
            WriteNote(vault, "Notes/Beta.md", "# Beta\n\nLinks to [[Alpha]].\n");
            WriteNote(vault, "Notes/Gamma.md", "---\ntags: [gamma]\naliases: [Third]\n---\n# Gamma\n\nStands alone.\n");
            WriteNote(vault, "Notes/Epsilon.md", "# Epsilon\n\nLinks to [[Alpha]] and is about to go.\n");
            WriteNote(vault, "Imported/proj/README.md", "# proj readme\n");
            // A name two notes share: a link to it must land where the full
            // index sends it, whichever that is.
            WriteNote(vault, "Notes/README.md", "# notes readme\n");
            File.WriteAllText(Path.Combine(vault, VaultIgnore.FileName), "Scratch/\n");
            var snapshot = Snapshot(vault);
            NodeSummary In(BrainExport e, string title) => e.Nodes.Single(n => n.Title == title);
            var alpha = In(snapshot, "Alpha").Id;
            var beta = In(snapshot, "Beta").Id;
            var gamma = In(snapshot, "Gamma").Id;
            var epsilon = In(snapshot, "Epsilon").Id;
            var alphaBacklinks = In(snapshot, "Alpha").BacklinkIds.ToList();

            var overlay = new ExportOverlay { ScanInterval = TimeSpan.Zero };
            Check("nothing changed: the snapshot itself comes back", ReferenceEquals(overlay.Apply(snapshot), snapshot));
            var generation = overlay.Generation;
            Check("…again, with no new generation", ReferenceEquals(overlay.Apply(snapshot), snapshot) && overlay.Generation == generation);

            WriteNote(vault, "Notes/Delta.md", "---\ntags: [proj]\n---\n# Delta\n\nSee [[Alpha]] and [[Third]]. #fresh\n");
            WriteNote(vault, "Notes/Zeta.md", "# Zeta\n\nFollows [[README]].\n");
            WriteNote(vault, "Notes/Beta.md", "# Beta\n\nNow links to [[Gamma]] instead. #edited\n");
            File.Delete(Path.Combine(vault, "Notes", "Epsilon.md"));
            WriteNote(vault, "Scratch/ignored.md", "# ignored\n");
            WriteNote(vault, ".obsidianx/cache.md", "# not a note\n");

            var view = overlay.Apply(snapshot);
            var delta = view.Nodes.FirstOrDefault(n => n.Title == "Delta");
            Check("a new note is in the view", delta != null, string.Join(", ", view.Nodes.Select(n => n.Title)));
            Check("…under the id the full index will give it",
                  delta?.Id == KnowledgeNode.IdFromPath(Path.Combine(vault, "Notes", "Delta.md")), delta?.Id);
            Check("…with its tags, its project scope, and its links — one of them by alias",
                  delta != null && delta.Tags.Contains("proj") && delta.Tags.Contains("fresh") && delta.Scope == "proj"
                  && delta.LinkedNodeIds.ToHashSet().SetEquals(new[] { alpha, gamma }),
                  delta == null ? "" : $"tags {string.Join(",", delta.Tags)} scope {delta.Scope} links {string.Join(",", delta.LinkedNodeIds)}");
            var editedBeta = In(view, "Beta");
            Check("an edited note keeps its id and reads as it is now",
                  editedBeta.Id == beta && editedBeta.Tags.Contains("edited") && editedBeta.LinkedNodeIds.SequenceEqual(new[] { gamma }),
                  $"{editedBeta.Id} {string.Join(",", editedBeta.Tags)} {string.Join(",", editedBeta.LinkedNodeIds)}");
            Check("a deleted note is gone, and nothing points at it",
                  view.Nodes.All(n => n.Id != epsilon && !n.LinkedNodeIds.Contains(epsilon) && !n.BacklinkIds.Contains(epsilon)
                                      && !n.AutoLinkedNodeIds.Contains(epsilon)));
            Check("backlinks follow the links written and removed since",
                  In(view, "Alpha").BacklinkIds.ToHashSet().SetEquals(new[] { delta?.Id ?? "" })
                  && In(view, "Gamma").BacklinkIds.ToHashSet().SetEquals(new[] { beta, delta?.Id ?? "" }),
                  $"alpha ← {string.Join(",", In(view, "Alpha").BacklinkIds)} · gamma ← {string.Join(",", In(view, "Gamma").BacklinkIds)}");
            Check("ignored files and the brain's own folder are not notes",
                  view.Nodes.All(n => !n.RelativePath.StartsWith("Scratch/") && !n.RelativePath.StartsWith(".obsidianx")));
            Check("the view says how far it is ahead of the snapshot",
                  view.Overlay is { Added: 2, Changed: 1, Removed: 1 } && view.TotalNotes == snapshot.TotalNotes + 1,
                  $"{view.Overlay?.Added}/{view.Overlay?.Changed}/{view.Overlay?.Removed} total {view.TotalNotes}");
            Check("the snapshot itself is untouched",
                  In(snapshot, "Alpha").BacklinkIds.SequenceEqual(alphaBacklinks) && !In(snapshot, "Beta").Tags.Contains("edited")
                  && snapshot.Nodes.Any(n => n.Id == epsilon) && snapshot.Overlay == null);
            Check("a changed view is a new generation", overlay.Generation > generation);
            Check("…and the same view until something changes again", ReferenceEquals(overlay.Apply(snapshot), view));

            // The whole point: the view and a full re-index of the same files agree
            // on everything a single note can tell.
            var full = Snapshot(vault);
            var mismatches = new List<string>();
            foreach (var f in full.Nodes)
            {
                var v = view.Nodes.FirstOrDefault(n => n.Id == f.Id);
                if (v == null) { mismatches.Add($"{f.Title}: missing"); continue; }
                if (v.Title != f.Title) mismatches.Add($"{f.Title}: title {v.Title}");
                if (!v.Tags.ToHashSet().SetEquals(f.Tags)) mismatches.Add($"{f.Title}: tags {string.Join(",", v.Tags)} vs {string.Join(",", f.Tags)}");
                if (!v.LinkedNodeIds.ToHashSet().SetEquals(f.LinkedNodeIds)) mismatches.Add($"{f.Title}: links");
                if (!v.BacklinkIds.ToHashSet().SetEquals(f.BacklinkIds)) mismatches.Add($"{f.Title}: backlinks");
                if (v.PrimaryCategory != f.PrimaryCategory) mismatches.Add($"{f.Title}: category {v.PrimaryCategory} vs {f.PrimaryCategory}");
                if (v.Kind != f.Kind || v.Scope != f.Scope) mismatches.Add($"{f.Title}: kind/scope");
                if (v.WordCount != f.WordCount) mismatches.Add($"{f.Title}: words");
                if (v.Preview != f.Preview) mismatches.Add($"{f.Title}: preview");
                if (v.ModifiedAt != f.ModifiedAt) mismatches.Add($"{f.Title}: modifiedAt");
            }
            if (view.Nodes.Count != full.Nodes.Count) mismatches.Add($"view has {view.Nodes.Count} notes, a full index {full.Nodes.Count}");
            Check("the view matches a full re-index note for note", mismatches.Count == 0, string.Join(" | ", mismatches));
            var zeta = In(view, "Zeta").LinkedNodeIds;
            Check("…including which of two same-named notes a link means",
                  zeta.Count == 1 && zeta.SequenceEqual(In(full, "Zeta").LinkedNodeIds), string.Join(",", zeta));
        }
        finally
        {
            try { Directory.Delete(vault, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static Task OverlayVanishGuard()
    {
        var vault = Path.Combine(Path.GetTempPath(), "brainx-vanish-" + Guid.NewGuid().ToString("N"));
        try
        {
            for (int i = 0; i < 30; i++) WriteNote(vault, $"Notes/n{i:D2}.md", $"# n{i}\n\nbody {i}\n");
            var snapshot = Snapshot(vault);
            var overlay = new ExportOverlay { ScanInterval = TimeSpan.Zero };
            for (int i = 0; i < 3; i++) File.Delete(Path.Combine(vault, "Notes", $"n{i:D2}.md"));
            var some = overlay.Apply(snapshot);
            Check("three deleted notes leave the view", some.Nodes.Count == 27 && some.Overlay?.Removed == 3, $"{some.Nodes.Count}");
            for (int i = 3; i < 25; i++) File.Delete(Path.Combine(vault, "Notes", $"n{i:D2}.md"));
            var most = overlay.Apply(snapshot);
            Check("most of the vault vanishing at once is not believed", most.Nodes.Count == 30, $"{most.Nodes.Count}");
        }
        finally
        {
            try { Directory.Delete(vault, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static async Task EmbedNoteNowChecks()
    {
        var (root, vault, _) = EmbedVault(0);
        var dir = Path.Combine(vault, ".obsidianx", "embeddings");
        string Note(string name, string text)
        {
            var p = Path.Combine(vault, "Notes", name + ".md");
            File.WriteAllText(p, text);
            return p;
        }
        bool HasVector(string id) => File.Exists(Path.Combine(dir, id + ".bin"))
                                     && new FileInfo(Path.Combine(dir, id + ".bin")).Length == FakeOllama.Dims * 4;
        try
        {
            using (var ok = new FakeOllama("bge-m3:latest"))
            {
                var svc = new EmbeddingService { OllamaUrl = ok.Url };
                var shortNote = Note("short", "# short\n\nfits in the head budget");
                var r1 = await svc.EmbedNoteNowAsync(vault, "e00000000001", "short", shortNote, 6000);
                Check("a note that fits: written, and final", r1 == "written via Ollama" && HasVector("e00000000001")
                      && !EmbeddingService.ReadPartial(dir).Contains("e00000000001"), r1);

                var longPath = Note("long", "# long\n\n" + new string('x', 7000));
                var r2 = await svc.EmbedNoteNowAsync(vault, "e00000000002", "long", longPath, 6000);
                Check("a note longer than the head: written, and marked for the next pass to finish",
                      r2.Contains("first") && HasVector("e00000000002") && EmbeddingService.ReadPartial(dir).Contains("e00000000002"), r2);
                File.WriteAllText(longPath, "# long\n\nshort now");
                var r3 = await svc.EmbedNoteNowAsync(vault, "e00000000002", "long", longPath, 6000);
                Check("…and once it fits, no longer partial", r3 == "written via Ollama" && !EmbeddingService.ReadPartial(dir).Contains("e00000000002"), r3);
                Check("the daemon did the embedding", ok.Embeds == 3, $"{ok.Embeds}");
            }

            using (var noModel = new FakeOllama("nomic-embed-text:latest"))
            {
                var svc = new EmbeddingService { OllamaUrl = noModel.Url };
                var p = Note("fallback", "# fallback\n\nbody");
                var skipped = await svc.EmbedNoteNowAsync(vault, "e00000000003", "fallback", p, 6000);
                Check("no model and no in-process fallback: skipped, saying why", skipped.Contains("not installed") && !HasVector("e00000000003"), skipped);
                var viaOnnx = await svc.EmbedNoteNowAsync(vault, "e00000000003", "fallback", p, 6000,
                    (_, tokens) => tokens == svc.InProcessMaxTokens ? new float[FakeOllama.Dims] : null);
                Check("with the in-process model: written provisionally, at the in-process budget",
                      viaOnnx.Contains("provisionally") && HasVector("e00000000003") && EmbeddingService.ReadPartial(dir).Contains("e00000000003"), viaOnnx);
                var wrongWidth = await svc.EmbedNoteNowAsync(vault, "e00000000004", "fallback", p, 6000, (_, _) => new float[3]);
                Check("a vector of the wrong width is never written", wrongWidth.Contains("does not match") && !File.Exists(Path.Combine(dir, "e00000000004.bin")), wrongWidth);
            }

            using (var ok = new FakeOllama("bge-m3:latest"))
            {
                var (root2, vault2, _) = EmbedVault(0, complete: false);
                try
                {
                    var p = Path.Combine(vault2, "Notes", "mid.md");
                    File.WriteAllText(p, "# mid\n\nbody");
                    var r = await new EmbeddingService { OllamaUrl = ok.Url }.EmbedNoteNowAsync(vault2, "e00000000005", "mid", p, 6000);
                    Check("mid-rebuild: nothing written, the pass owns the vault", r.Contains("in progress")
                          && !File.Exists(Path.Combine(vault2, ".obsidianx", "embeddings", "e00000000005.bin")) && ok.Embeds == 0, r);
                }
                finally { try { Directory.Delete(root2, recursive: true); } catch { } }

                var bare = Path.Combine(Path.GetTempPath(), "brainx-bare-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(bare);
                    var r = await new EmbeddingService { OllamaUrl = ok.Url }.EmbedNoteNowAsync(bare, "e00000000006", "x", Path.Combine(bare, "x.md"), 6000);
                    Check("a vault that never embedded is not started by a write", r.Contains("no embeddings") && !Directory.Exists(Path.Combine(bare, ".obsidianx")), r);
                }
                finally { try { Directory.Delete(bare, recursive: true); } catch { } }
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task LiveIndexEndToEnd()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }
        var (root, vault, _) = EmbedVault(0);
        WriteNote(vault, "Notes/Alpha.md", "# Alpha\n\nThe first note.\n");
        WriteNote(vault, "Notes/Beta.md", "# Beta\n\nA note about the wombatcast.\n");
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(Snapshot(vault)));
        var alphaId = KnowledgeNode.IdFromPath(Path.Combine(vault, "Notes", "Alpha.md"));
        var betaId = KnowledgeNode.IdFromPath(Path.Combine(vault, "Notes", "Beta.md"));

        using var ollama = new FakeOllama("bge-m3:latest");
        var env = new Dictionary<string, string> { ["BRAINX_OLLAMA_URL"] = ollama.Url };
        Process? a = null, b = null;
        try
        {
            a = await StartServer(exe, vault, env);
            b = await StartServer(exe, vault, env);
            var rpc = 10;
            async Task<JObject> Call(Process server, string tool, JObject args) =>
                ToolJson(await Rpc(server, rpc++, "tools/call", new JObject { ["name"] = tool, ["arguments"] = args }));
            static List<string> Ids(JObject search) =>
                (search["results"] as JArray)?.Select(r => r["id"]?.ToString() ?? "").ToList() ?? new();
            async Task<JObject> SearchUntil(Process server, string query, Func<List<string>, bool> done)
            {
                JObject last = new();
                for (int i = 0; i < 20; i++)
                {
                    last = await Call(server, "brain_search", new JObject { ["query"] = query });
                    if (done(Ids(last))) break;
                    await Task.Delay(300);
                }
                return last;
            }

            var before = await Call(b, "brain_search", new JObject { ["query"] = "quokkaflux" });
            Check("before the write, another session finds nothing", Ids(before).Count == 0, before.ToString());

            var created = await Call(a, "brain_create_note", new JObject
            {
                ["title"] = "Quokka flux", ["tags"] = "probe",
                ["content"] = "The quokkaflux lives here. It builds on [[Alpha]]."
            });
            var id = created["id"]?.ToString() ?? "";
            Check("create_note says the note is searchable now", created["hint"]?.ToString().Contains("Searchable now") == true, created["hint"]?.ToString());
            var read = await Call(a, "brain_get_note", new JObject { ["id"] = id });
            Check("the id create_note returned reads back at once", read["content"]?.ToString().Contains("quokkaflux") == true, read.ToString());
            var appended = await Call(a, "brain_append_note", new JObject { ["id"] = id, ["content"] = "A second line." });
            Check("…and takes an append by that id", appended["success"]?.Value<bool>() == true, appended.ToString());

            var found = await SearchUntil(b, "quokkaflux", ids => ids.Contains(id));
            Check("another session finds it — the same query it answered 'nothing' to a moment ago", Ids(found).Contains(id), found.ToString());

            // Supersession between two notes neither of which the snapshot has.
            var v2 = await Call(a, "brain_create_note", new JObject
            {
                ["title"] = "Quokka flux revised", ["supersedes"] = "[[Quokka flux]]",
                ["content"] = "The quokkaflux moved; this replaces the first note."
            });
            Check("a supersedes: naming a note written minutes ago resolves",
                  v2["supersedes"]?[0]?["status"]?.ToString() == "ok" && v2["supersedes"]?[0]?["resolved"]?.ToString() == id,
                  v2["supersedes"]?.ToString());
            var demoted = await Call(a, "brain_search", new JObject { ["query"] = "quokkaflux", ["compact"] = false, ["bypass_cache"] = true });
            var old = (demoted["results"] as JArray)?.OfType<JObject>().FirstOrDefault(r => r["id"]?.ToString() == id);
            Check("…and search marks the older one superseded at once", old?["superseded"]?.Value<bool>() == true, demoted.ToString());
            var backlinks = await Call(b, "brain_get_backlinks", new JObject { ["id"] = alphaId });
            Check("the note it links to lists it as a backlink", backlinks.ToString().Contains(id), backlinks.ToString());
            // The notes written here, and whatever else the servers wrote at start
            // (the Codex rules installer drops an AGENTS.md at the vault root).
            var newOnDisk = Directory.GetFiles(vault, "*.md", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(vault, f).Replace('\\', '/'))
                .Where(r => !r.StartsWith(".obsidianx/") && r is not ("Notes/Alpha.md" or "Notes/Beta.md")).ToList();
            // Another session's write shows up on this one's next scan — within
            // the overlay's two-second interval, not necessarily this instant.
            JObject stats = new();
            for (int i = 0; i < 15; i++)
            {
                stats = await Call(b, "brain_stats", new JObject());
                if (stats["index"]?["sinceSnapshot"]?["added"]?.Value<int>() == newOnDisk.Count) break;
                await Task.Delay(300);
            }
            Check("brain_stats says how far the live view is ahead of the snapshot",
                  newOnDisk.Contains("Notes/Quokka flux.md")
                  && stats["index"]?["sinceSnapshot"]?["added"]?.Value<int>() == newOnDisk.Count,
                  stats["index"]?["sinceSnapshot"]?.ToString(Newtonsoft.Json.Formatting.None) + " · new on disk: " + string.Join(", ", newOnDisk));

            var sidecar = Path.Combine(vault, ".obsidianx", "embeddings", id + ".bin");
            for (int i = 0; i < 40 && !File.Exists(sidecar); i++) await Task.Delay(250);
            Check("its vector is written without waiting for a Garden pass",
                  File.Exists(sidecar) && new FileInfo(sidecar).Length == FakeOllama.Dims * 4 && ollama.Embeds > 0, $"embeds {ollama.Embeds}");

            File.Delete(Path.Combine(vault, "Notes", "Beta.md"));
            var gone = await SearchUntil(b, "wombatcast", ids => !ids.Contains(betaId));
            Check("a deleted note stops being found", !Ids(gone).Contains(betaId), gone.ToString());

            // Two servers started on a temp vault, and the machine's owner has
            // nothing new: no Claude Code memory folder named after it, no
            // AGENTS.md from the Codex installer (BRAINX_SANDBOX).
            var slug = vault.TrimEnd('\\', '/').Replace(":", "-").Replace("\\", "-").Replace("/", "-");
            var memoryDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", slug);
            Check("a sandboxed server leaves nothing outside its vault", !Directory.Exists(memoryDir)
                  && !File.Exists(Path.Combine(vault, "AGENTS.md")), memoryDir);
        }
        finally
        {
            foreach (var s in new[] { a, b })
            {
                try { s?.Kill(entireProcessTree: true); } catch { }
                s?.Dispose();
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
