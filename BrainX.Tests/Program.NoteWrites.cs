using System.Diagnostics;
using System.Text;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// What brain_create_note writes (2026-09-23). Of the newest 400 notes in
/// Notes/, 123 carried their title twice and 75 carried a second frontmatter
/// block whose tags never became tags; 94 note names could not be reached by a
/// [[link]]; and exists-then-write let a second agent silently replace the
/// first's note.
/// </summary>
internal static partial class Program
{
    private static void RegisterNoteWriteChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("create_note content: one frontmatter, one title, the agent's tags kept", NoteNormalizerChecks));
        checks.Add(("create_note file names: every title gets a name a [[link]] can reach", SafeFileNameChecks));
        checks.Add(("brain_create_note end to end: reshaped on disk, and two racing writers cannot both win", CreateNoteEndToEnd));
        checks.Add(("brain_remember lands in a note the indexer reads; the backfill recovers the old ones once", RememberChecks));
        checks.Add(("import_path: two sources with one folder+file name no longer overwrite each other", ImportCollisionChecks));
        checks.Add(("create_note hygiene: a near-identical Thai title is flagged as a possible duplicate", ThaiDuplicateChecks));
        checks.Add(("mark_verified: the finding is stored on the note, and a later check clears it", MarkVerifiedChecks));
        checks.Add(("supersede: dream proposes a note's replacement, the owner's one call writes it — nothing before", SupersedeChecks));
    }

    private static async Task SupersedeChecks()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }

        // The frontmatter edit itself, on every shape a list can already have.
        var add = System.Reflection.Assembly.LoadFrom(Path.ChangeExtension(exe, ".dll")).GetType("BrainX.Mcp.Program")!
            .GetMethod("AddFrontmatterListItem", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        string Add(string text) => (string)add.Invoke(null, [text, "supersedes", "\"[[Old]]\""])!;
        Check("block list: one more line, the rest as written",
              Add("---\nsupersedes:\n  - \"[[A]]\"\ntags: [x]\n---\nbody") == "---\nsupersedes:\n  - \"[[A]]\"\n  - \"[[Old]]\"\ntags: [x]\n---\nbody");
        Check("flow list: one more element", Add("---\nsupersedes: [\"[[A]]\"]\n---\nbody") == "---\nsupersedes: [\"[[A]]\", \"[[Old]]\"]\n---\nbody");
        Check("scalar: becomes a list of both", Add("---\nsupersedes: \"[[A]]\"\n---\nbody") == "---\nsupersedes:\n  - \"[[A]]\"\n  - \"[[Old]]\"\n---\nbody");
        Check("no key: added before the closing fence", Add("---\ncreated: x\n---\nbody") == "---\ncreated: x\nsupersedes:\n  - \"[[Old]]\"\n---\nbody");
        Check("no frontmatter: one is created", Add("# T\nbody") == "---\nsupersedes:\n  - \"[[Old]]\"\n---\n\n# T\nbody");
        Check("CRLF stays CRLF", Add("---\r\ncreated: x\r\n---\r\nbody") == "---\r\ncreated: x\r\nsupersedes:\r\n  - \"[[Old]]\"\r\n---\r\nbody");

        var root = Path.Combine(Path.GetTempPath(), "brainx-supersede-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var emb = Path.Combine(vault, ".obsidianx", "embeddings");
        Directory.CreateDirectory(emb);
        void Note(string rel, string created, string body)
        {
            var p = Path.Combine(vault, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, $"---\ncreated: {created}\ntags: [deploy]\n---\n{body}\n");
        }
        Note("Notes/Deploy checklist for staging.md", "2026-09-01T09:00:00Z", "# Deploy checklist for staging\n\nOld steps.");
        Note("Notes/Deploy checklist for staging v2.md", "2026-09-12T09:00:00Z", "# Deploy checklist for staging v2\n\nNew steps.");
        Note("Notes/Claude-Sessions/Session 2026-09-13 deploy checklist for staging.md", "2026-09-13T09:00:00Z", "# s\n\nlog");
        Note("Imported/proj/Deploy checklist for staging.md", "2026-09-14T09:00:00Z", "# imported\n\ncopy");
        Note("Notes/Quarterly revenue model.md", "2026-09-15T09:00:00Z", "# Quarterly revenue model\n\nunrelated title");
        // Two halves of one piece of work: alike, but written together.
        Note("Notes/Cache warmup notes part one.md", "2026-09-16T09:00:00Z", "# one\n\nfirst half");
        Note("Notes/Cache warmup notes part two.md", "2026-09-16T15:00:00Z", "# two\n\nsecond half");
        // Reports BrainX rewrites in place: each is replaced by its own next run.
        foreach (var (name, day) in new[] { ("Retrieval benchmark", "2026-09-02"), ("Retrieval benchmark — gold-paraphrase", "2026-09-17") })
        {
            var p = Path.Combine(vault, "Notes", name + ".md");
            File.WriteAllText(p, $"---\ncreated: {day}T09:00:00Z\nsource: brainx-eval\ntags:\n  - eval\n---\n# {name}\n\nnumbers\n");
        }
        var graph = new KnowledgeIndexer().IndexVault(vault);
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(
            BrainExporter.BuildExport(new BrainX.Core.Models.BrainIdentity { Address = "t", DisplayName = "t" }, graph, vault)));
        // Every note on one direction: only the rules can tell them apart.
        var rng = new Random(3);
        var basis = Enumerable.Range(0, 32).Select(_ => (float)rng.NextDouble()).ToArray();
        foreach (var n in graph.Nodes)
        {
            var v = basis.Select(x => x + (float)(rng.NextDouble() * 0.02)).ToArray();
            var bytes = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(emb, n.Id + ".bin"), bytes);
        }
        var older2 = graph.Nodes.Single(n => n.Title == "Deploy checklist for staging" && n.FilePath.Contains("Notes")).Id;
        var newer = graph.Nodes.Single(n => n.Title == "Deploy checklist for staging v2").Id;
        Process? server = null;
        try
        {
            var dream = JObject.Parse(RunCli(exe, $"dream --vault \"{vault}\""));
            var candidates = (dream["proposals"] as JArray)!.OfType<JObject>().Where(p => p["kind"]?.ToString() == "supersede-candidate").ToList();
            Check("dream proposes the rewrite as a replacement — and nothing else: not the session log, the import, the "
                  + "unrelated title, two halves written the same day, or two reports BrainX rewrites in place",
                  candidates.Count == 1 && candidates[0]["noteId"]?.ToString() == newer && candidates[0]["action"]?.ToString().Contains(older2) == true,
                  string.Join(" | ", candidates.Select(c => c["subject"] + " / " + c["evidence"])));

            server = await StartServer(exe, vault, new Dictionary<string, string> { ["BRAINX_CANARY"] = "0" });
            var file = Path.Combine(vault, "Notes", "Deploy checklist for staging v2.md");
            var before = File.ReadAllText(file);
            var preview = ToolJson(await Rpc(server, 10, "tools/call", new JObject
            {
                ["name"] = "brain_apply_audit_fix",
                ["arguments"] = new JObject { ["kind"] = "supersede", ["newer"] = newer, ["older"] = older2 }
            }));
            Check("the owner's call is a dry run until they say otherwise — preview shown, file untouched",
                  preview["dryRun"]?.Value<bool>() == true && preview["frontmatter"]?.ToString().Contains("supersedes:") == true
                  && File.ReadAllText(file) == before, preview.ToString());

            var applied = ToolJson(await Rpc(server, 11, "tools/call", new JObject
            {
                ["name"] = "brain_apply_audit_fix",
                ["arguments"] = new JObject { ["kind"] = "supersede", ["newer"] = newer, ["older"] = older2, ["dryRun"] = false }
            }));
            var after = File.ReadAllText(file);
            Check("dryRun=false writes it, keeping the note's own frontmatter and body",
                  applied["changed"]?.Value<bool>() == true && after.Contains("created: 2026-09-12T09:00:00Z") && after.Contains("New steps."),
                  after);
            // The title is shared with the imported copy, so the reference is the id.
            Check("…by id when the title would name two notes",
                  new KnowledgeIndexer().ReadOne(file, vault).Properties.TryGetValue("supersedes", out var sup)
                  && sup is System.Collections.IEnumerable list && list.Cast<object>().Select(o => o.ToString()).Contains(older2), after);

            var again = ToolJson(await Rpc(server, 12, "tools/call", new JObject { ["name"] = "brain_dream", ["arguments"] = new JObject() }));
            Check("…and the proposal is gone once answered",
                  !(again["proposals"] as JArray)!.Any(p => p["kind"]?.ToString() == "supersede-candidate"), again["proposals"]?.ToString());
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task MarkVerifiedChecks()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }
        var root = Path.Combine(Path.GetTempPath(), "brainx-verify-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var note = Path.Combine(vault, "Notes", "Deploy port.md");
        Directory.CreateDirectory(Path.GetDirectoryName(note)!);
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        File.WriteAllText(note, "---\ntags: [deploy]\nverifyCmd: curl -s localhost:8080/health\n---\n# Deploy port\n\nThe app listens on 8080.\n");
        var graph = new KnowledgeIndexer().IndexVault(vault);
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(
            BrainExporter.BuildExport(new BrainX.Core.Models.BrainIdentity { Address = "t", DisplayName = "t" }, graph, vault)));
        var id = graph.Nodes.Single().Id;
        Process? server = null;
        try
        {
            server = await StartServer(exe, vault);
            var finding = "port: 9090 now, not 8080 — see \"ops\" note\nand a second line";
            var failed = ToolJson(await Rpc(server, 10, "tools/call", new JObject
                { ["name"] = "brain_mark_verified", ["arguments"] = new JObject { ["id"] = id, ["ok"] = false, ["note"] = finding } }));
            Check("mark_verified answers", failed["success"]?.Value<bool>() == true, failed.ToString());
            var props = new KnowledgeIndexer().ReadOne(note, vault).Properties;
            Check("the finding is on the note, and reads back exactly — colon, quotes, newline and all",
                  props.TryGetValue("verifyNote", out var stored) && stored?.ToString() == finding, File.ReadAllText(note));
            Check("…beside the status, with the note's other frontmatter intact",
                  props.GetValueOrDefault("verifyStatus")?.ToString() == "failed"
                  && props.GetValueOrDefault("verifyCmd")?.ToString() == "curl -s localhost:8080/health"
                  && File.ReadAllText(note).Contains("The app listens on 8080."));

            await Rpc(server, 11, "tools/call", new JObject
                { ["name"] = "brain_mark_verified", ["arguments"] = new JObject { ["id"] = id, ["ok"] = true } });
            var after = new KnowledgeIndexer().ReadOne(note, vault).Properties;
            Check("a later check with no finding clears the old one", after.GetValueOrDefault("verifyStatus")?.ToString() == "ok"
                  && !after.ContainsKey("verifyNote"), File.ReadAllText(note));
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task ThaiDuplicateChecks()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }
        var root = Path.Combine(Path.GetTempPath(), "brainx-dup-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        var existing = new NodeSummary
        {
            Id = "d00000000001", Title = "แก้การ์ดทับกันตอนย่อหน้าต่าง", RelativePath = "Notes/แก้การ์ดทับกันตอนย่อหน้าต่าง.md",
            ModifiedAt = DateTime.UtcNow, Tags = ["hud"]
        };
        File.WriteAllText(Path.Combine(vault, existing.RelativePath), "# แก้การ์ดทับกันตอนย่อหน้าต่าง\n\nเนื้อหา");
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(
            new BrainExport { VaultPath = vault, GeneratedAt = DateTime.UtcNow, TotalNotes = 1, Nodes = [existing] }));
        Process? server = null;
        try
        {
            server = await StartServer(exe, vault);
            // One word inserted INSIDE the Thai run — no space, as Thai is
            // written. Split on spaces, the two titles share no token at all.
            var created = ToolJson(await Rpc(server, 10, "tools/call", new JObject
            {
                ["name"] = "brain_create_note",
                ["arguments"] = new JObject { ["title"] = "แก้ปัญหาการ์ดทับกันตอนย่อหน้าต่าง", ["content"] = "สรุปรอบสอง" }
            }));
            var dups = created["hygiene"]?["possibleDuplicates"] as JArray;
            Check("a Thai title with one word added inside it is a possible duplicate",
                  dups?.OfType<JObject>().Any(d => d["id"]?.ToString() == existing.Id) == true, created["hygiene"]?.ToString());
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static Task ImportCollisionChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-import-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        string Source(string repo, string file, string text)
        {
            var p = Path.Combine(root, repo, "docs", file);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
            return p;
        }
        ScanHit Hit(string source, string hash) => new()
        {
            SourcePath = source, FileName = Path.GetFileName(source), ContentHash = hash, SizeBytes = 32,
            ModifiedAt = DateTime.UtcNow, SuggestedVaultPath = Path.Combine("Imported", "docs", Path.GetFileName(source))
        };
        string SourceOf(string note) => File.ReadAllLines(note).FirstOrDefault(l => l.StartsWith("source: "))?[8..] ?? "";
        try
        {
            Directory.CreateDirectory(vault);
            var a = Source("repoA", "guide.md", "alpha guide");
            var b = Source("repoB", "guide.md", "beta guide");
            var importer = new VaultImporter();
            var opts = new ImportOptions { VaultPath = vault, Mode = VaultImporter.ImportMode.Reference };

            var first = importer.Import([Hit(a, "ha"), Hit(b, "hb")], opts);
            var notes = first.Imported.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Check("both namesakes imported, to two different notes", first.Imported.Count == 2 && notes.Count == 2, string.Join(" | ", first.Imported));
            Check("…the second one under its own name, and said so", first.Renamed.Count == 1, string.Join(" | ", first.Renamed));
            Check("…each note still carries its own source",
                  notes.Count == 2 && notes.Select(SourceOf).Distinct().Count() == 2, string.Join(" | ", notes.Select(SourceOf)));

            var again = importer.Import([Hit(a, "ha"), Hit(b, "hb")], opts);
            Check("re-import of unchanged sources: both skipped, nothing new written",
                  again.Skipped.Count == 2 && again.Imported.Count == 0 && again.Renamed.Count == 0,
                  $"skipped {again.Skipped.Count}, imported {again.Imported.Count}");
            var changed = importer.Import([Hit(b, "hb2")], opts);
            Check("a changed source updates ITS note, not its namesake's",
                  changed.Imported.Count == 1 && SourceOf(changed.Imported[0]).Replace('/', '\\').Equals(b, StringComparison.OrdinalIgnoreCase),
                  string.Join(" | ", changed.Imported));

            // A note the owner wrote at an import path is not the importer's.
            var own = Path.Combine(vault, "Imported", "docs", "todo.md");
            File.WriteAllText(own, "# my own todo\n");
            var c = Source("repoC", "todo.md", "vendor todo");
            var third = importer.Import([Hit(c, "hc")], opts);
            Check("a hand-written note at the same path is never overwritten", File.ReadAllText(own) == "# my own todo\n");
            Check("…the import goes beside it", third.Imported.Count == 1 && !third.Imported[0].Equals(own, StringComparison.OrdinalIgnoreCase));

            // Before this fix a namesake could already have overwritten a note
            // the manifest still lists — that source must come back.
            var preferredA = Path.Combine(vault, "Imported", "docs", "guide.md");
            var holder = SourceOf(preferredA).Replace('/', '\\').Equals(a, StringComparison.OrdinalIgnoreCase) ? a : b;
            var lost = holder == a ? b : a;
            foreach (var n in Directory.GetFiles(Path.Combine(vault, "Imported", "docs"), "guide (*).md")) File.Delete(n);
            var recovered = importer.Import([Hit(lost, lost == a ? "ha" : "hb2")], opts);
            Check("a source the manifest lists but whose note is gone is imported again",
                  recovered.Imported.Count == 1 && File.Exists(recovered.Imported[0]), string.Join(" | ", recovered.Imported.Concat(recovered.Skipped)));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static async Task RememberChecks()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-remember-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var sessions = Path.Combine(vault, ".obsidianx", "sessions");
        Directory.CreateDirectory(sessions);
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"),
            Newtonsoft.Json.JsonConvert.SerializeObject(new BrainExport { VaultPath = vault, GeneratedAt = DateTime.UtcNow }));
        Process? server = null;
        try
        {
            server = await StartServer(exe, vault);
            var first = ToolJson(await Rpc(server, 10, "tools/call", new JObject
                { ["name"] = "brain_remember", ["arguments"] = new JObject { ["text"] = "The HUD planner is pure geometry." } }));
            await Rpc(server, 11, "tools/call", new JObject
                { ["name"] = "brain_remember", ["arguments"] = new JObject { ["text"] = "Second fact\nwith two lines." } });
            var rel = first["path"]?.ToString() ?? "";
            var note = Path.Combine(vault, rel);
            Check("brain_remember writes outside .obsidianx/, where the indexer reads",
                  rel.StartsWith("Notes/Remembered/Remembered ", StringComparison.Ordinal) && File.Exists(note), first.ToString());
            var text = File.Exists(note) ? File.ReadAllText(note) : "";
            Check("…one note per month, tagged, with one dated section per fact",
                  text.Contains("  - remembered\n") && text.Split("\n## ").Length == 3 && text.Contains("Second fact\nwith two lines."), text);
            Check("…and the session journal still has it", Directory.GetFiles(sessions, "*.md")
                  .Any(f => File.ReadAllText(f).Contains("The HUD planner is pure geometry.")));
        }
        finally
        {
            try { server?.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
        }

        // The backfill, on journals written the old way — one of them named in
        // the Buddhist era, as a th-TH process once named them.
        var legacy = Path.Combine(root, "legacy");
        var legacySessions = Path.Combine(legacy, ".obsidianx", "sessions");
        Directory.CreateDirectory(legacySessions);
        File.WriteAllText(Path.Combine(legacySessions, "2569-08-10.md"),
            "# journal\n\n> **REMEMBER** `09:15:00`  \n> first fact\n> continued\n\nsome log line\n\n> **REMEMBER** `21:40:07`  \n> second fact\n");
        File.WriteAllText(Path.Combine(legacySessions, "2026-09-01.md"), "> **REMEMBER** `08:00:00`  \n> third fact\n");
        try
        {
            var dry = RunCli(exe, $"remember-backfill --vault \"{legacy}\"");
            var remembered = Path.Combine(legacy, "Notes", "Remembered");
            Check("backfill dry run: counts all three", dry.Contains("remembered facts: 3"), dry);
            Check("…and writes nothing", !Directory.Exists(remembered));

            var applied = RunCli(exe, $"remember-backfill --vault \"{legacy}\" --apply");
            var aug = Path.Combine(remembered, "Remembered 2026-08.md");
            var sep = Path.Combine(remembered, "Remembered 2026-09.md");
            Check("backfill --apply: a Buddhist-era journal lands in its Gregorian month", File.Exists(aug), applied);
            Check("…with multi-line facts whole", File.Exists(aug) && File.ReadAllText(aug).Contains("## 2026-08-10 09:15\n\nfirst fact\ncontinued\n"),
                  File.Exists(aug) ? File.ReadAllText(aug) : "");
            Check("…and each month in its own note", File.Exists(sep) && File.ReadAllText(sep).Contains("third fact"));

            var before = File.ReadAllText(aug) + File.ReadAllText(sep);
            var again = RunCli(exe, $"remember-backfill --vault \"{legacy}\" --apply");
            Check("backfill is idempotent: a second --apply adds nothing",
                  again.Contains("nothing to add") && File.ReadAllText(aug) + File.ReadAllText(sep) == before, again);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string RunCli(string exe, string arguments)
    {
        var psi = new ProcessStartInfo(exe, arguments)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        psi.Environment.Remove(StubMcpServer.EnvFlag);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60_000);
        return stdout + stderr;
    }

    private static Task NoteNormalizerChecks()
    {
        var agent = "---\r\ntitle: Fix the thing\r\ncreated: 2020-01-01\r\ntags: [brainx, hud]\r\nstatus: shipped\r\nlinks:\r\n  - a\r\n  - b\r\n---\r\n\r\n# Fix the thing\r\n\r\nThe body.\r\n";
        var r = NoteNormalizer.Normalize(agent, "Fix the thing");
        Check("frontmatter: tags from an inline list", r.Tags.SequenceEqual(new[] { "brainx", "hud" }), string.Join(",", r.Tags));
        Check("frontmatter: other keys kept verbatim, nested lines with them",
              r.ExtraFrontmatter.SequenceEqual(new[] { "title: Fix the thing", "status: shipped", "links:", "  - a", "  - b" }),
              string.Join(" | ", r.ExtraFrontmatter));
        Check("frontmatter: the agent's created: is not copied over the real one", !r.ExtraFrontmatter.Any(l => l.StartsWith("created")));
        Check("title: the repeated '# title' is dropped", r.DroppedTitleHeading && r.WriteTitleHeading && r.Body == "The body.", r.Body);
        Check("line endings: \\n only", !r.Body.Contains('\r'));

        var listTags = NoteNormalizer.Normalize("---\ntags:\n  - '#one'\n  - two\n---\nbody", "T");
        Check("frontmatter: tags from a YAML list, '#' stripped", listTags.Tags.SequenceEqual(new[] { "one", "two" }), string.Join(",", listTags.Tags));

        var own = NoteNormalizer.Normalize("# A different heading\n\ntext", "The title");
        Check("title: a different leading H1 stays, and the server writes none", !own.WriteTitleHeading && own.Body.StartsWith("# A different heading"));

        var rule = NoteNormalizer.Normalize("---\nThis is prose after a rule.\n---\nmore", "T");
        Check("a horizontal rule followed by prose is not mistaken for frontmatter",
              !rule.HadFrontmatter && rule.Body.StartsWith("---\nThis is prose"), rule.Body);

        var sup = NoteNormalizer.Normalize("---\nsupersedes:\n  - \"[[Old, but gold]]\"\n  - abcdef012345\n---\nx", "T");
        Check("supersedes: a title with a comma stays one reference",
              sup.Supersedes.SequenceEqual(new[] { "[[Old, but gold]]", "abcdef012345" }), string.Join(" | ", sup.Supersedes));

        var plain = NoteNormalizer.Normalize("Just a body.", "T");
        Check("plain content passes through untouched", plain.Body == "Just a body." && !plain.HadFrontmatter && plain.WriteTitleHeading && !plain.DroppedTitleHeading);
        return Task.CompletedTask;
    }

    private static Task SafeFileNameChecks()
    {
        Check("C# keeps its look, loses its meaning to [[links]]", NoteNormalizer.SafeFileName("ElectroBench — C# + Avalonia") == "ElectroBench — C＃ + Avalonia");
        Check("[brackets] and ^ too", NoteNormalizer.SafeFileName("[SKIP] a^b") == "［SKIP］ a＾b");
        Check("characters Windows refuses are removed", NoteNormalizer.SafeFileName("a/b:c?d*e<f>g\"h") == "abcdefgh");
        Check("trailing dots and spaces are trimmed (Windows would drop them silently)", NoteNormalizer.SafeFileName("Plan v2. ") == "Plan v2");
        Check("runs of spaces collapse", NoteNormalizer.SafeFileName("a   b  c") == "a b c", NoteNormalizer.SafeFileName("a   b  c"));
        Check("control characters (tab, newline) are removed like other invalid ones",
              NoteNormalizer.SafeFileName("a\tb\nc") == "abc", NoteNormalizer.SafeFileName("a\tb\nc"));
        Check("Thai is left alone", NoteNormalizer.SafeFileName("บันทึก การ์ด HUD") == "บันทึก การ์ด HUD");
        return Task.CompletedTask;
    }

    private static async Task CreateNoteEndToEnd()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the end-to-end check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-create-e2e-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"),
            Newtonsoft.Json.JsonConvert.SerializeObject(new BrainExport { VaultPath = vault, GeneratedAt = DateTime.UtcNow }));

        var servers = new List<Process>();
        try
        {
            for (int s = 0; s < 2; s++) servers.Add(await StartServer(exe, vault));

            var created = ToolJson(await Rpc(servers[0], 10, "tools/call", new JObject
            {
                ["name"] = "brain_create_note",
                ["arguments"] = new JObject
                {
                    ["title"] = "C# card layout",
                    ["tags"] = "hud",
                    ["content"] = "---\ntags: [layout, brainx]\nstatus: shipped\n---\n\n# C# card layout\n\nCards stop overlapping.\n"
                }
            }));
            var path = Path.Combine(vault, "Notes", "C＃ card layout.md");
            Check("create_note succeeds", created["success"]?.Value<bool>() == true, created.ToString());
            Check("…at a file name a [[link]] can reach", File.Exists(path), string.Join(", ", Directory.GetFiles(Path.Combine(vault, "Notes"))));
            Check("…and says which link that is", created["wikiLink"]?.ToString() == "[[C＃ card layout]]", created["wikiLink"]?.ToString());
            Check("…and what it reshaped", (created["reshaped"] as JArray)?.Count >= 2, created["reshaped"]?.ToString());

            var text = File.Exists(path) ? File.ReadAllText(path) : "";
            var fences = text.Split('\n').Count(l => l.TrimEnd() == "---");
            Check("on disk: exactly one frontmatter block", fences == 2, text);
            Check("on disk: the agent's tags are real tags", text.Contains("  - layout\n") && text.Contains("  - brainx\n") && text.Contains("  - hud\n"), text);
            Check("on disk: the agent's other keys survive", text.Contains("status: shipped\n"), text);
            Check("on disk: one H1, carrying the real title", text.Split('\n').Count(l => l.StartsWith("# ")) == 1 && text.Contains("\n# C# card layout\n"), text);

            // Two writers, one title, the same instant.
            var calls = servers.Select((srv, k) => Rpc(srv, 20 + k, "tools/call", new JObject
            {
                ["name"] = "brain_create_note",
                ["arguments"] = new JObject { ["title"] = "Race", ["content"] = $"written by server {k}" }
            })).ToArray();
            var answers = (await Task.WhenAll(calls)).Select(ToolJson).ToList();
            var wins = answers.Count(a => a["success"]?.Value<bool>() == true);
            var refusals = answers.Count(a => a.ToString().Contains("already exists"));
            Check("two writers racing on one title: exactly one wins", wins == 1, string.Join(" || ", answers.Select(a => a.ToString(Newtonsoft.Json.Formatting.None))));
            Check("…and the other is told, not silently overwritten", refusals == 1);
            var raced = File.ReadAllText(Path.Combine(vault, "Notes", "Race.md"));
            Check("…and the surviving file is one writer's whole note", raced.Contains("written by server 0") ^ raced.Contains("written by server 1"), raced);
            Check("…with no temp file left behind", !Directory.GetFiles(Path.Combine(vault, "Notes"), "*.tmp").Any());
        }
        finally
        {
            foreach (var s in servers)
            {
                try { s.Kill(entireProcessTree: true); } catch { }
                s.Dispose();
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task<Process> StartServer(string exe, string vault, IReadOnlyDictionary<string, string>? env = null)
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
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        var server = Process.Start(psi)!;
        server.StandardInput.AutoFlush = true;
        _ = Task.Run(async () => { try { while (await server.StandardError.ReadLineAsync() != null) { } } catch { } });
        await Rpc(server, 1, "initialize", new JObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JObject(),
            ["clientInfo"] = new JObject { ["name"] = "brainx-tests", ["version"] = "1" }
        });
        await server.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        return server;
    }
}
