using System.Diagnostics;
using BrainX.Core.Services;
using BrainX.Server.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

internal static partial class Program
{
    private static void RegisterCodeGraphChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("code graph: git HEAD, path containment and file freshness without a process", CodeGraphPlumbingChecks));
        checks.Add(("code graph: traversal is directed, typed, deterministic and bounded", CodeGraphTraceChecks));
        checks.Add(("code graph: Graphify import and trace through the real MCP, search left intact", CodeGraphEndToEnd));
        checks.Add(("code graph: native C# build, cache, rebind and trace through MCP", CodeGraphNativeBuild));
        checks.Add(("code graph: syntax languages keep receivers, imports and deep files through code-build", CodeGraphSyntaxBuild));
        checks.Add(("code graph: what the Universe and its dashboard are told", CodeGraphViewShapes));
    }

    private static async Task CodeGraphSyntaxBuild()
    {
        var exe = FindMcpExe() ?? throw new InvalidOperationException("Build brainx-mcp first");
        var (root, vault, _) = EmbedVault(0);
        var repo = Path.Combine(root, "sample");
        var files = new Dictionary<string, string>
        {
            ["a.js"] = "function send() {}\nclass Svc { run() { this.client.send(); } }\nconst {helper} = require('./lib');\nfunction go(){ helper(); }\n",
            ["lib.js"] = "function helper() {}\nmodule.exports = {helper};\n",
            ["deep.js"] = "const x = " + string.Join(" + ", Enumerable.Repeat("a", 60000)) + ";\nfunction afterDeep() {}\n",
            ["b.ts"] = "import { Base } from './base';\nexport abstract class Kid extends Base implements Face { hi() { this.up(); } }\ninterface Face {}\n",
            ["base.ts"] = "export class Base { up() {} }\n",
            ["src/pkg/__init__.py"] = "",
            ["src/pkg/store.py"] = "def save():\n    pass\n",
            ["src/main.py"] = "from pkg.store import save as keep\ndef run():\n    keep()\n",
            ["app/Models/User.php"] = "<?php\nnamespace App\\Models;\nclass User { public function save() { $this->touch(); } public function touch() {} }\n",
            ["app/Http/Ctl.php"] = "<?php\nnamespace App\\Http;\nuse App\\Models\\User;\nclass Ctl { function go() { $u = new User(); } }\n",
            ["packages/core/index.js"] = "export function inMono() {}\n",
        };
        foreach (var (path, text) in files)
        {
            var full = Path.Combine(repo, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        var thaiFolder = Path.Combine(root, "\u0E42\u0E04\u0E49\u0E14");
        Directory.CreateDirectory(thaiFolder);
        File.WriteAllText(Path.Combine(thaiFolder, "x.py"), "def only():\n    pass\n");
        async Task<(int Code, JObject? Line)> Build(params string[] args)
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "code-build" }.Concat(args).Concat(["--vault", vault])) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync();
            var line = (await p.StandardOutput.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
            await stderr;
            return (p.ExitCode, line.Length == 0 ? null : JObject.Parse(line.Split('\n')[^1]));
        }
        try
        {
            var (code, line) = await Build(repo, "--repo", "sample");
            Check("code-build exits 0 and reports one line of JSON", code == 0 && line?["status"]?.ToString() == "built", line?.ToString());
            var g = CodeGraphStore.Load(vault, "sample") ?? throw new InvalidOperationException("no graph");
            var named = g.Symbols.Where(s => s.Kind != "file").ToLookup(s => s.Name);
            bool Linked(string relation, string from, string to) => g.Edges.Any(e => e.Relation == relation
                && named[from].Any(s => s.Id == e.From) && named[to].Any(s => s.Id == e.To));
            Check("a 60,000-term expression does not end the build", named["afterDeep"].Any());
            Check("packages/ is scanned unless it is a NuGet folder", named["inMono"].Any());
            Check("this.client.send() is not the top-level send", !Linked("calls", "run", "send"));
            Check("destructured require reaches the other file", Linked("calls", "go", "helper"));
            Check("TS extends through an import", Linked("inherits", "Kid", "Base"));
            Check("TS abstract class implements its interface", Linked("implements", "Kid", "Face"));
            Check("Python aliased import reaches the function", Linked("calls", "run", "save"));
            Check("PHP $this->touch() binds in its class", Linked("calls", "save", "touch"));
            Check("PHP new binds through use", Linked("creates", "go", "User"));
            var stamp = File.GetLastWriteTimeUtc(CodeGraphStore.PathFor(vault, "sample"));
            (code, line) = await Build(repo, "--repo", "sample");
            Check("an unchanged repo leaves the file alone", code == 0 && line?["status"]?.ToString() == "unchanged"
                && File.GetLastWriteTimeUtc(CodeGraphStore.PathFor(vault, "sample")) == stamp, line?.ToString());
            (code, line) = await Build(thaiFolder);
            Check("a folder name with no usable letters still gets a slug", code == 0
                && line?["repo"]?.ToString()?.StartsWith("repo-", StringComparison.Ordinal) == true, line?.ToString());
            var noCode = Path.Combine(root, "no-code");
            Directory.CreateDirectory(noCode);
            File.WriteAllText(Path.Combine(noCode, "readme.md"), "# nothing to read\n");
            (code, line) = await Build(noCode, "--repo", "no-code");
            Check("a folder with no code saves no graph and says so", code == 1 && line?["status"]?.ToString() == "empty"
                && !File.Exists(CodeGraphStore.PathFor(vault, "no-code")), line?.ToString());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static Task CodeGraphViewShapes()
    {
        CodeSymbol Sym(string id, string name, string kind = "method", string file = "a.cs") =>
            new() { Id = id, Name = name, Kind = kind, File = file, Line = 1 };
        var g = new CodeGraph
        {
            Repo = "r", Commit = "abc", ImportedAt = new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc),
            Source = new CodeGraphSource { Kind = "brainx" },
            Symbols = [Sym("f", "a.cs", "file"), Sym("run", "Run"), Sym("go", "Go"), Sym("gone", "Goner", file: "zebra/Run.cs"), Sym("x", "Runner")],
            Edges =
            [
                new CodeEdge { From = "run", To = "go", Relation = "calls", Provenance = "EXTRACTED", Lines = [3, 9] },
                new CodeEdge { From = "x", To = "go", Relation = "calls", Provenance = "INFERRED" },
                new CodeEdge { From = "f", To = "run", Relation = "contains", Provenance = "EXTRACTED" },
            ],
            Mentions = [new CodeMention { NoteId = "n1", NotePath = "Notes/n.md", SymbolId = "go", Count = 2, Text = "Go" }]
        };
        var layer = CodeGraphView.Layer(g, "r");
        var drawn = ((JArray)layer["nodes"]!).Select(n => n["id"]!.ToString()).ToHashSet();
        Check("the layer holds the mentioned symbol and its file", drawn.Contains("go") && drawn.Contains("f") && !drawn.Contains("gone"), string.Join(",", drawn));
        Check("the layer's nodes name their repo", layer["nodes"]![0]!["repo"]?.ToString() == "r");
        var bare = new CodeGraph { Repo = "b", Symbols = [Sym("f", "a.cs", "file"), Sym("run", "Run")] };
        Check("a graph no note names still draws its files", ((JArray)CodeGraphView.Layer(bare, "b")["nodes"]!).Count == 1);

        Check("draw key: same layer, same key", CodeGraphView.DrawKey(layer) == CodeGraphView.DrawKey((JObject)layer.DeepClone()));
        Check("draw key: nothing drawn is empty", CodeGraphView.DrawKey(new JObject { ["nodes"] = new JArray() }) == "");

        var over = CodeGraphView.Overview(g);
        Check("overview counts provenance from the edges", over["provenance"]?["EXTRACTED"]?.Value<int>() == 2 && over["provenance"]?["INFERRED"]?.Value<int>() == 1);
        Check("overview sorts relations by count", ((JObject)over["relations"]!).Properties().First().Name == "calls");
        var hubs = (JArray)over["hubs"]!;
        Check("the hub is the most connected symbol and files are left out", hubs[0]["id"]?.ToString() == "go" && hubs[0]["in"]?.Value<int>() == 2
            && hubs.All(h => h["kind"]?.ToString() != "file"), hubs.ToString());

        var found = CodeGraphView.Search(g, "run");
        var order = ((JArray)found["hits"]!).Select(h => h["id"]!.ToString()).ToList();
        Check("search: whole name, then prefix, then file", order.SequenceEqual(["run", "x", "gone"]), string.Join(",", order));
        Check("search: empty asks for nothing", CodeGraphView.Search(g, "  ")["total"]?.Value<int>() == 0);

        var detail = CodeGraphView.Detail(g, "go")!;
        var calledBy = ((JArray)detail["groups"]!).Single(x => x["label"]?.ToString() == "Called by");
        Check("detail: callers come extracted first", calledBy["total"]?.Value<int>() == 2 && calledBy["items"]![0]!["id"]?.ToString() == "run"
            && calledBy["items"]![0]!["sites"]?.Value<int>() == 2, calledBy.ToString());
        Check("detail: the note that names it", detail["notes"]![0]!["noteId"]?.ToString() == "n1" && detail["notesTotal"]?.Value<int>() == 1);
        Check("detail: an unknown symbol is null, not a throw", CodeGraphView.Detail(g, "nope") == null && CodeGraphView.Detail(g, null) == null);

        var card = CodeGraphView.Card(g, "r", 3);
        CodeGraphView.MarkState(card);
        Check("a card with no folder is gone", card["state"]?.ToString() == "gone");

        var (root, vault, _) = EmbedVault(0);
        try
        {
            var one = Path.Combine(root, "one", "app");
            var two = Path.Combine(root, "two", "app");
            Directory.CreateDirectory(one);
            Directory.CreateDirectory(two);
            Check("slug: a new folder takes its name", CodeGraphStore.SlugFor(vault, one) == "app");
            CodeGraphStore.Save(vault, new CodeGraph { Repo = "app", RepoRoot = one });
            Check("slug: the same folder keeps its name", CodeGraphStore.SlugFor(vault, one + Path.DirectorySeparatorChar) == "app");
            var other = CodeGraphStore.SlugFor(vault, two);
            Check("slug: another folder of that name does not replace it", other.StartsWith("app-", StringComparison.Ordinal) && other == CodeGraphStore.NormaliseRepo(other), other);
            Check("slug: dots and underscores cannot start one", CodeGraphStore.SlugFor(vault, Path.Combine(root, "_a..b")) == "a.b");
            File.WriteAllText(CodeGraphStore.PathFor(vault, "app"), "{ not json");
            Check("slug: an unreadable graph is replaced, it holds nothing", CodeGraphStore.SlugFor(vault, two) == "app");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static async Task CodeGraphNativeBuild()
    {
        var exe = FindMcpExe() ?? throw new InvalidOperationException("Build brainx-mcp first");
        var (root, vault, _) = EmbedVault(0);
        var repo = Path.Combine(root, "sample");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(repo, "A.cs"), "class A\n{\n    public void Run()\n    {\n        B.Go();\n        B.Go();\n    }\n}\n");
        var b = Path.Combine(repo, "B.cs");
        File.WriteAllText(b, "static class B { public static void Go() {} }\n");
        WriteNote(vault, "Notes/Call flow.md", "# Call flow\nA.Run invokes B.Go. The `Go` method is the same target.\n");
        File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), JsonConvert.SerializeObject(Snapshot(vault)));
        Process? server = null;
        try
        {
            server = await StartServer(exe, vault);
            var rpc = 300;
            async Task<JObject> Call(string name, JObject args) => ToolJson(await Rpc(server, rpc++, "tools/call",
                new JObject { ["name"] = name, ["arguments"] = args }));
            var args = new JObject { ["repo_root"] = repo, ["repo"] = "sample" };
            var built = await Call("brain_code_build", args);
            Check("native build reads both C# files", built["status"]?.ToString() == "built"
                && built["filesRead"]?.Value<int>() == 2, built.ToString());
            var graph = CodeGraphStore.Load(vault, "sample")!;
            var call = graph.Edges.SingleOrDefault(e => e.Relation == "calls"
                && graph.Index.ById[e.From].Name == "Run" && graph.Index.ById[e.To].Name == "Go");
            Check("native build binds to B.Go and keeps both call sites", call?.Provenance == "EXTRACTED"
                && call.Lines?.SequenceEqual([5, 6]) == true, call == null ? "no edge" : JsonConvert.SerializeObject(call));
            Check("native build links a note to exact code symbols", graph.Mentions.Count == 2
                && graph.Mentions.Any(m => m.Text == "A.Run") && graph.Mentions.Any(m => m.Text == "B.Go"), JsonConvert.SerializeObject(graph.Mentions));
            var trace = await Call("brain_code_trace", new JObject { ["repo"] = "sample", ["symbol"] = "A.Run", ["relations"] = "calls", ["notes"] = 0 });
            Check("native graph is available through the existing trace", trace["status"]?.ToString() == "ok"
                && (trace["edges"] as JArray)?.Count == 1, trace.ToString());
            Check("trace reports exact linked notes", (trace["linkedNotes"] as JArray)?.Any(n => n["writtenAs"]?.ToString() == "A.Run") == true);
            var fromNote = await Call("brain_code_trace", new JObject { ["repo"] = "sample", ["note_id"] = "Notes/Call flow.md", ["notes"] = 0 });
            Check("note seeds its named code symbols", fromNote["status"]?.ToString() == "ok"
                && fromNote["seedsFrom"]?.ToString() == "note-mentions" && (fromNote["seeds"] as JArray)?.Count == 2, fromNote.ToString());
            var same = await Call("brain_code_build", args);
            Check("second build uses the persisted cache", same["status"]?.ToString() == "unchanged"
                && same["filesRead"]?.Value<int>() == 0, same.ToString());
            File.WriteAllText(b, "static class B { public static void Other() {} }\n");
            var changed = await Call("brain_code_build", args);
            graph = CodeGraphStore.Load(vault, "sample")!;
            Check("removed target rebinds its unchanged caller", changed["status"]?.ToString() == "refreshed"
                && changed["filesRead"]?.Value<int>() == 1 && changed["filesRebound"]?.Value<int>() == 1
                && !graph.Edges.Any(e => e.Relation == "calls" && graph.Index.ById[e.From].Name == "Run"), changed.ToString());
            File.WriteAllText(Path.Combine(repo, "lib.js"), "export function helper() {}\nexport class JsBase {}\n");
            File.WriteAllText(Path.Combine(repo, "main.js"), "import { helper, JsBase } from './lib.js';\nexport function runJs() { helper(); }\nexport class JsChild extends JsBase {}\n");
            File.WriteAllText(Path.Combine(repo, "main.ts"), "export function runTs() { doneTs(); }\nfunction doneTs() {}\n");
            File.WriteAllText(Path.Combine(repo, "main.tsx"), "function helperTsx() { return <div />; }\nexport function View() { return helperTsx(); }\n");
            File.WriteAllText(Path.Combine(repo, "lib.py"), "def helper_py():\n    pass\n");
            File.WriteAllText(Path.Combine(repo, "main.py"), "from .lib import helper_py\n\ndef run_py():\n    helper_py()\n    done_py()\n\ndef done_py():\n    pass\n\nclass PyBase: pass\nclass PyChild(PyBase): pass\n");
            File.WriteAllText(Path.Combine(repo, "main.php"), "<?php\nfunction run_php() { done_php(); }\nfunction done_php() {}\nclass PhpBase {}\nclass PhpChild extends PhpBase {}\n");
            var mixed = await Call("brain_code_build", args);
            graph = CodeGraphStore.Load(vault, "sample")!;
            Check("tree-sitter reads JavaScript, TypeScript, Python and PHP", mixed["status"]?.ToString() == "refreshed"
                && graph.Issues.Languages.GetValueOrDefault("javascript") == 2
                && graph.Issues.Languages.GetValueOrDefault("typescript") == 1
                && graph.Issues.Languages.GetValueOrDefault("tsx") == 1
                && graph.Issues.Languages.GetValueOrDefault("python") == 2
                && graph.Issues.Languages.GetValueOrDefault("php") == 1
                && graph.Issues.SkippedFiles.Count == 0, mixed.ToString());
            bool HasCall(string from, string to) => graph.Edges.Any(e => e.Relation == "calls"
                && graph.Index.ById[e.From].Name == from && graph.Index.ById[e.To].Name == to);
            Check("syntax graph resolves local and imported calls", HasCall("runJs", "helper")
                && HasCall("runTs", "doneTs") && HasCall("run_py", "done_py")
                && HasCall("run_py", "helper_py") && HasCall("View", "helperTsx")
                && HasCall("run_php", "done_php"), JsonConvert.SerializeObject(graph.Issues));
            bool HasBase(string child, string parent) => graph.Edges.Any(e => e.Relation == "inherits"
                && graph.Index.ById[e.From].Name == child && graph.Index.ById[e.To].Name == parent);
            Check("syntax graph records class inheritance", HasBase("JsChild", "JsBase")
                && HasBase("PyChild", "PyBase") && HasBase("PhpChild", "PhpBase"), JsonConvert.SerializeObject(graph.Issues));
            File.WriteAllText(Path.Combine(repo, "C.cs"), "class C { void Run() { D.Select(1); } }\n");
            var overload = Path.Combine(repo, "D.cs");
            File.WriteAllText(overload, "static class D { public static void Select(long x) {} }\n");
            await Call("brain_code_build", args);
            File.WriteAllText(overload, "static class D { public static void Select(long x) {} public static void Select(int x) {} }\n");
            var rebound = await Call("brain_code_build", args);
            graph = CodeGraphStore.Load(vault, "sample")!;
            var selected = graph.Edges.Where(e => e.Relation == "calls" && graph.Index.ById[e.From].Name == "Run")
                .Select(e => graph.Index.ById[e.To]).FirstOrDefault(s => s.Name == "Select");
            Check("new overload rebinds an unchanged caller", rebound["filesRebound"]?.Value<int>() >= 1
                && selected?.ExternalId.Contains("System.Int32", StringComparison.Ordinal) == true, rebound.ToString());
            Check("native build stays unavailable remotely", !McpRemotePolicy.IsAllowed("brain_code_build", McpScope.ReadWrite));
        }
        finally
        {
            try { server?.Kill(true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private const string FixtureCommit = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";

    /// <summary>
    /// A repository with a .git, two Python files and a graph.json in the shape
    /// graphifyy 0.9.99 writes (field names and label forms copied from its
    /// release asset): functions "name()", a method ".name()", a C#-style
    /// qualified "Store.flush()", a class, a file node, an external module, a
    /// rationale, a type stub with no file, a path outside the scan root, and an
    /// edge to a node that does not exist.
    /// </summary>
    private static string WriteGraphifyFixture(string repo)
    {
        Directory.CreateDirectory(Path.Combine(repo, ".git", "refs", "heads"));
        File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(repo, ".git", "refs", "heads", "main"), FixtureCommit + "\n");
        File.WriteAllText(Path.Combine(repo, "app.py"), "import lib\n\ndef run_job():\n    load_config()\n    write_log()\n\n\n\ndef process_item():\n    pass\n\nclass FileStore(lib.BaseStore):\n    pass\n");
        File.WriteAllText(Path.Combine(repo, "lib.py"), "def load_config():\n    \"\"\"config loads once so run_job stays fast\"\"\"\n\n\ndef write_log():\n    pass\n\n\ndef process_item():\n    pass\n\nclass BaseStore:\n    def save(self): pass\n");
        static JObject N(string id, string label, string file, string? loc, params (string K, JToken V)[] extra)
        {
            var o = new JObject { ["id"] = id, ["label"] = label, ["_origin"] = "ast", ["community"] = 0, ["file_type"] = "code",
                                  ["norm_label"] = label.ToLowerInvariant(), ["source_file"] = file, ["source_location"] = loc };
            foreach (var (k, v) in extra) o[k] = v;
            return o;
        }
        static JObject L(string s, string t, string rel, string file, string loc, string conf = "EXTRACTED", double score = 1.0) =>
            new() { ["source"] = s, ["target"] = t, ["relation"] = rel, ["_origin"] = "ast", ["confidence"] = conf,
                    ["confidence_score"] = score, ["source_file"] = file, ["source_location"] = loc, ["weight"] = 1.0 };
        var graph = new JObject
        {
            ["directed"] = false, ["multigraph"] = false,
            ["graph"] = new JObject { ["schema_version"] = 1, ["graphify_version"] = "0.9.99" },
            ["nodes"] = new JArray(
                N("app", "app.py", "app.py", "L1"),
                N("app_run_job", "run_job()", "app.py", "L3", ("_callable", true)),
                N("app_process_item", "process_item()", "app.py", "L9", ("_callable", true)),
                N("app_filestore", "FileStore", "app.py", "L12", ("_callable", true), ("_callable_class", true)),
                N("lib", "lib.py", "lib.py", "L1"),
                N("lib_load_config", "load_config()", "lib.py", "L1", ("_callable", true)),
                N("lib_write_log", "write_log()", "lib.py", "L5", ("_callable", true)),
                N("lib_process_item", "process_item()", "lib.py", "L9", ("_callable", true)),
                N("lib_basestore", "BaseStore", "lib.py", "L12", ("_callable", true), ("_callable_class", true)),
                N("lib_basestore_save", ".save()", "lib.py", "L13", ("_callable", true)),
                N("lib_store_flush", "Store.flush()", "lib.py", "L13", ("_callable", true)),
                N("lib_rationale_2", "config loads once so run_job stays fast", "lib.py", "L2", ("file_type", "rationale")),
                N("os", "os", "", null, ("external", true), ("type", "external"), ("file_type", "concept")),
                N("lib_py_path", "Path", "", ""),
                N("outside_x", "x()", "../outside.py", "L1", ("_callable", true))),
            ["links"] = new JArray(
                L("app", "app_run_job", "contains", "app.py", "L3"),
                L("app_run_job", "lib_load_config", "calls", "app.py", "L4"),
                L("app_run_job", "lib_write_log", "calls", "app.py", "L5", "INFERRED", 0.85),
                L("app", "lib", "imports_from", "app.py", "L1"),
                L("app_filestore", "lib_basestore", "inherits", "app.py", "L12"),
                L("lib_basestore", "lib_basestore_save", "method", "lib.py", "L13"),
                L("lib", "os", "imports", "lib.py", "L1"),
                L("lib_rationale_2", "lib_load_config", "rationale_for", "lib.py", "L2"),
                L("lib_load_config", "lib_py_path", "references", "lib.py", "L1"),
                L("app_run_job", "ghost_node", "calls", "app.py", "L6")),
            ["hyperedges"] = new JArray(),
            ["built_at_commit"] = FixtureCommit
        };
        var outDir = Path.Combine(repo, "graphify-out");
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, "graph.json");
        // Graphify writes ASCII-escaped compact JSON; Newtonsoft reads either.
        File.WriteAllText(path, graph.ToString(Formatting.None));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        return path;
    }

    private static async Task CodeGraphEndToEnd()
    {
        var exe = FindMcpExe() ?? throw new InvalidOperationException("Build brainx-mcp first");
        var (root, vault, _) = EmbedVault(0);
        var repo = Path.Combine(root, "repo");
        var graphPath = WriteGraphifyFixture(repo);
        using var ollama = new FakeOllama("bge-m3:latest") { Fixed = [1, 1, 1, 1, 1, 1, 1, 0] };
        Process? server = null;
        try
        {
            WriteNote(vault, "Notes/Config decision.md", "# Config decision\n\nload_config is cached because run_job ran it per item.\n");
            WriteNote(vault, "Notes/เหตุผลเรื่องคอนฟิก.md", "# เหตุผลเรื่องคอนฟิก\n\nเราโหลดค่าตั้งครั้งเดียว ไม่ให้งานช้า load_config\n");
            // No word in common with any query below: only the vector finds it.
            WriteNote(vault, "Notes/Hidden.md", "# Hidden\n\nA semantic-only answer.\n");
            for (int i = 0; i < 40; i++) WriteNote(vault, $"Noise/Item{i}.md", $"# Item{i}\n\nordinary filler row{i}\n");
            var snapshot = Snapshot(vault);
            var hidden = snapshot.Nodes.Single(n => n.Title == "Hidden").Id;
            var thai = snapshot.Nodes.Single(n => n.Title == "เหตุผลเรื่องคอนฟิก").Id;
            var config = snapshot.Nodes.Single(n => n.Title == "Config decision").Id;
            foreach (var n in snapshot.Nodes)
            {
                // The Thai note is about the question, so a real embedder puts it
                // near the query; the English one only shares identifiers with it.
                var v = n.Id == hidden ? new float[] { 1, 1, 1, 1, 1, 1, 1, 0 }
                      : n.Id == thai ? new float[] { 1, 1, 1, 1, 1, 1, 0, 0 }
                      : new float[] { 0, 0, 0, 0, 0, 0, 0, 1 };
                var bytes = new byte[v.Length * 4];
                Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
                File.WriteAllBytes(Path.Combine(vault, ".obsidianx", "embeddings", n.Id + ".bin"), bytes);
            }
            File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), JsonConvert.SerializeObject(snapshot));
            // Escalation stays on, as it ships: the notes arm follows brain_search's rule.
            server = await StartServer(exe, vault, new Dictionary<string, string>
            {
                ["BRAINX_CANARY"] = "0", ["BRAINX_OLLAMA_URL"] = ollama.Url, ["BRAINX_OVERLAY"] = "0"
            });
            int rpc = 10;
            async Task<JObject?> Raw(string tool, JObject args) => await Rpc(server!, rpc++, "tools/call", new JObject { ["name"] = tool, ["arguments"] = args });
            async Task<JObject> Call(string tool, JObject args) => ToolJson(await Raw(tool, args));
            async Task<JToken> Search(string q) => new JArray(((await Call("brain_search", new JObject { ["query"] = q, ["limit"] = 50, ["bypass_cache"] = true }))["results"] as JArray ?? [])
                .Select(r => new JObject { ["id"] = r["id"], ["score"] = r["score"] }));

            var searchBefore = new[] { await Search("load_config"), await Search("เหตุผลเรื่องคอนฟิก"), await Search("filler") };
            var empty = await Call("brain_code_trace", new JObject { ["symbol"] = "run_job" });
            Check("trace before any import says no-graph and how to get one",
                empty["status"]?.ToString() == "no-graph" && empty["advice"]?.ToString().Contains("brain_code_import") == true, empty.ToString());

            var listed = await Rpc(server, rpc++, "tools/list", new JObject());
            var names = (listed?["result"]?["tools"] as JArray ?? []).Select(t => t["name"]?.ToString()).ToList();
            Check("both tools are advertised", names.Contains("brain_code_trace") && names.Contains("brain_code_import"));

            var imported = await Call("brain_code_import", new JObject { ["path"] = repo });
            Check("import: a repo folder finds graphify-out/graph.json", imported["status"]?.ToString() == "imported" && imported["repo"]?.ToString() == "repo", imported.ToString());
            Check("import: version, commit and the scan root are recorded",
                imported["source"]?["version"]?.ToString() == "0.9.99" && imported["commit"]?.ToString() == FixtureCommit
                && string.Equals(imported["repoRoot"]?.ToString(), repo, StringComparison.OrdinalIgnoreCase));
            Check("import: an edge to a missing node is dropped and counted",
                imported["missingEndpoints"]?.Value<int>() == 1 && imported["missingEndpointSamples"]?[0]?.ToString().Contains("ghost_node") == true);
            Check("import: a path outside the scan root is not followed", imported["pathsOutsideRepo"]?.Value<int>() == 1);
            Check("import: Graphify's one-edge-per-pair limit is stated",
                (imported["warnings"] as JArray ?? []).Any(w => w.ToString().Contains("one edge per pair")), imported["warnings"]?.ToString());
            Check("import: nothing was stale on arrival", imported["filesNewerThanGraph"]?.Value<int>() == 0);
            Check("import: provenance tallies come from the file", imported["provenance"]?["INFERRED"]?.Value<int>() == 1);

            var callees = await Call("brain_code_trace", new JObject { ["symbol"] = "run_job", ["direction"] = "callees", ["relations"] = "calls", ["notes"] = 0 });
            var calleeEdges = callees["edges"] as JArray ?? [];
            Check("trace: callees of run_job, EXTRACTED first, with file:line",
                calleeEdges.Count == 2 && calleeEdges[0]["toName"]?.ToString() == "load_config" && calleeEdges[0]["provenance"]?.ToString() == "EXTRACTED"
                && calleeEdges[0]["file"]?.ToString() == "app.py" && calleeEdges[0]["line"]?.Value<int>() == 4
                && calleeEdges[1]["provenance"]?.ToString() == "INFERRED" && calleeEdges[1]["confidence"]?.Value<double>() == 0.85, callees.ToString());
            Check("trace: a fresh graph on its own commit is current and unchanged",
                callees["graph"]?["state"]?.ToString() == "current" && calleeEdges.All(e => e["freshness"]?.ToString() == "unchanged") && callees["stale"] == null);
            Check("trace: a symbol id names the repo, path, name and line",
                callees["seeds"]?[0]?.ToString() == "repo:app.py#run_job@L3", callees["seeds"]?.ToString());

            var callers = await Call("brain_code_trace", new JObject { ["symbol"] = "load_config", ["direction"] = "callers", ["notes"] = 0 });
            Check("trace: callers include run_job and the rationale that explains it",
                (callers["edges"] as JArray ?? []).Any(e => e["fromName"]?.ToString() == "run_job" && e["relation"]?.ToString() == "calls")
                && (callers["nodes"] as JArray ?? []).Any(n => n["kind"]?.ToString() == "rationale"), callers.ToString());
            var bases = await Call("brain_code_trace", new JObject { ["symbol"] = "FileStore", ["direction"] = "callees", ["relations"] = "inherits", ["notes"] = 0 });
            Check("trace: inherits points subclass → base", (bases["edges"] as JArray)?.SingleOrDefault()?["toName"]?.ToString() == "BaseStore", bases.ToString());
            var method = await Call("brain_code_trace", new JObject { ["symbol"] = "BaseStore.save", ["notes"] = 0 });
            Check("trace: Class.method resolves through the method edge", method["status"]?.ToString() == "ok", method.ToString());
            var qualified = await Call("brain_code_trace", new JObject { ["symbol"] = "Store.flush", ["notes"] = 0 });
            Check("trace: a C#-style qualified label resolves by its qualified name",
                qualified["status"]?.ToString() == "ok" && qualified["nodes"]?[0]?["name"]?.ToString() == "flush", qualified.ToString());

            var amb = await Call("brain_code_trace", new JObject { ["symbol"] = "process_item" });
            Check("trace: a repeated name is ambiguous, lists both, walks nothing",
                amb["status"]?.ToString() == "ambiguous" && amb["ambiguity"]?["count"]?.Value<int>() == 2 && (amb["edges"] as JArray)?.Count == 0, amb.ToString());
            var picked = await Call("brain_code_trace", new JObject { ["symbol"] = "lib.py#process_item", ["notes"] = 0 });
            Check("trace: path#name settles it", picked["status"]?.ToString() == "ok" && picked["seeds"]?[0]?.ToString() == "repo:lib.py#process_item@L9");
            var cut = await Call("brain_code_trace", new JObject { ["symbol"] = "run_job", ["max_edges"] = 1, ["notes"] = 0 });
            Check("trace: max_edges is honoured and reported", (cut["edges"] as JArray)?.Count == 1 && cut["truncatedBy"]?.ToString() == "edges");
            foreach (var budget in new[] { 400, 700, 1500 })
            {
                var raw = await Raw("brain_code_trace", new JObject { ["symbol"] = "load_config", ["max_tokens"] = budget, ["notes"] = 5, ["hops"] = 3 });
                var text = raw?["result"]?["content"]?[0]?["text"]?.ToString() ?? "";
                var body = JObject.Parse(text);
                var printed = text.Length / 4.0;
                var estimated = body["estimatedTokens"]?.Value<int>() ?? 0;
                Check($"budget {budget}: the printed answer fits and the reported estimate is close",
                    printed <= budget * 1.05 && Math.Abs(estimated - printed) <= printed * 0.15,
                    $"printed≈{printed:0} estimated={estimated} edges={(body["edges"] as JArray)?.Count} notes={(body["notes"] as JArray)?.Count} cut={body["truncatedBy"]}/{body["notesTruncatedBy"]}");
            }

            var why = await Call("brain_code_trace", new JObject { ["query"] = "ทำไม load_config ถูกเรียกจาก run_job ครั้งเดียว", ["notes"] = 5 });
            var whyNotes = (why["notes"] as JArray ?? []).Select(n => n["id"]?.ToString()).ToList();
            Check("query: identifiers in a Thai question seed the walk", why["seedsFrom"]?.ToString() == "query-identifiers"
                && (why["seeds"] as JArray)?.Count == 2, why["seeds"]?.ToString());
            Check("query: a question keyword cannot cover escalates; the Thai note and the semantic-only note both come back",
                whyNotes.Contains(thai) && whyNotes.Contains(hidden) && why["notesMode"]?.ToString() == "escalated:hybrid",
                $"mode={why["notesMode"]} notes={string.Join(",", whyNotes)} thai={thai} hidden={hidden}");
            var bySymbol = await Call("brain_code_trace", new JObject { ["symbol"] = "load_config", ["notes"] = 3 });
            var symbolNotes = (bySymbol["notes"] as JArray ?? []).Select(n => n["id"]?.ToString()).ToList();
            Check("symbol: an identifier is a lookup, so notes naming it come from keyword, English and Thai alike",
                bySymbol["notesMode"]?.ToString() == "keyword" && bySymbol["notesQuery"]?.ToString() == "load_config"
                && symbolNotes.Contains(config) && symbolNotes.Contains(thai) && !symbolNotes.Contains(hidden),
                $"mode={bySymbol["notesMode"]} notes={string.Join(",", symbolNotes)}");
            var scoped = await Call("brain_code_trace", new JObject { ["symbol"] = "load_config", ["notes"] = 3, ["scope"] = "Noise" });
            var scopedPaths = (scoped["notes"] as JArray ?? []).Select(n => n["path"]?.ToString() ?? "").ToList();
            Check("symbol: the notes scope is strict, never broadened",
                scopedPaths.All(p => p.StartsWith("Noise/", StringComparison.Ordinal)), string.Join(",", scopedPaths));
            var plain = await Call("brain_code_trace", new JObject { ["query"] = "why does the job run", ["notes"] = 0 });
            Check("query: plain English words are not taken for identifiers", plain["status"]?.ToString() == "not-found" && (plain["seeds"] as JArray)?.Count == 0, plain.ToString());

            // The checkout moves on: one cited file edited, HEAD advanced.
            File.AppendAllText(Path.Combine(repo, "lib.py"), "\ndef added(): pass\n");
            File.WriteAllText(Path.Combine(repo, ".git", "refs", "heads", "main"), new string('9', 40) + "\n");
            var moved = await Call("brain_code_trace", new JObject { ["symbol"] = "run_job", ["relations"] = "calls", ["direction"] = "callees", ["notes"] = 0 });
            Check("stale: HEAD past the graph's commit is reported", moved["graph"]?["state"]?.ToString() == "behind-head" && moved["stale"] != null, moved.ToString());
            Check("stale: an edited file marks its symbols, an untouched one stays unchanged",
                (moved["nodes"] as JArray ?? []).Any(n => n["file"]?.ToString() == "lib.py" && n["freshness"]?.ToString() == "changed")
                && (moved["edges"] as JArray ?? []).All(e => e["freshness"]?.ToString() == "unchanged"));

            var secret = Path.Combine(root, "not-a-graph.json");
            File.WriteAllText(secret, "SECRET-TOKEN-123 this is not json");
            var bad = await Raw("brain_code_import", new JObject { ["path"] = secret, ["repo"] = "bad" });
            var badText = bad?["result"]?["content"]?[0]?["text"]?.ToString() ?? "";
            Check("import: a non-graph file is refused without echoing its content",
                bad?["result"]?["isError"]?.Value<bool>() == true && !badText.Contains("SECRET-TOKEN-123") && badText.Contains("not valid JSON"), badText);
            var future = JObject.Parse(File.ReadAllText(graphPath));
            future["graph"]!["schema_version"] = 2;
            var futurePath = Path.Combine(root, "future.json");
            File.WriteAllText(futurePath, future.ToString());
            var refused = await Raw("brain_code_import", new JObject { ["path"] = futurePath, ["repo"] = "future", ["repo_root"] = repo });
            Check("import: a newer schema_version is refused, not guessed at",
                refused?["result"]?["isError"]?.Value<bool>() == true && refused?["result"]?["content"]?[0]?["text"]?.ToString().Contains("schema_version 2") == true);
            var traversal = await Raw("brain_code_import", new JObject { ["path"] = graphPath, ["repo"] = "../escape" });
            Check("import: a repo slug cannot name a path", traversal?["result"]?["isError"]?.Value<bool>() == true
                && !File.Exists(Path.Combine(vault, ".obsidianx", "escape.json")));
            Check("import: the store holds exactly the good graph", CodeGraphStore.List(vault).SequenceEqual(["repo"]), string.Join(",", CodeGraphStore.List(vault)));

            var searchAfter = new[] { await Search("load_config"), await Search("เหตุผลเรื่องคอนฟิก"), await Search("filler") };
            Check("search: brain_search answers exactly as before the import",
                searchBefore.Zip(searchAfter).All(p => JToken.DeepEquals(p.First, p.Second)) && searchBefore[1].Any(), string.Join(" | ", searchAfter.Select(s => s.ToString(Formatting.None))));
            Check("policy: import never crosses the network; trace stays local for now",
                !McpRemotePolicy.IsAllowed("brain_code_import", McpScope.ReadWrite) && !McpRemotePolicy.IsAllowed("brain_code_trace", McpScope.Read));
        }
        finally
        {
            try { server?.Kill(true); } catch { }
            server?.Dispose();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static Task CodeGraphPlumbingChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-codegraph-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string sha1 = "1111111111111111111111111111111111111111";
            const string sha2 = "2222222222222222222222222222222222222222";
            var repo = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(repo, ".git", "refs", "heads"));
            File.WriteAllText(Path.Combine(repo, ".git", "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(repo, ".git", "refs", "heads", "main"), sha1 + "\n");
            Check("HEAD resolves a loose branch ref", GitHead.Read(repo) == sha1, GitHead.Read(repo));

            File.Delete(Path.Combine(repo, ".git", "refs", "heads", "main"));
            File.WriteAllText(Path.Combine(repo, ".git", "packed-refs"), "# pack-refs with: peeled\n" + sha2 + " refs/heads/main\n");
            Check("HEAD falls back to packed-refs", GitHead.Read(repo) == sha2, GitHead.Read(repo));

            // A worktree: .git is a file, HEAD in its own dir, branch refs in the common dir.
            var wt = Path.Combine(root, "wt");
            var wtGit = Path.Combine(repo, ".git", "worktrees", "wt");
            Directory.CreateDirectory(wt);
            Directory.CreateDirectory(wtGit);
            File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + wtGit + "\n");
            File.WriteAllText(Path.Combine(wtGit, "HEAD"), "ref: refs/heads/main\n");
            File.WriteAllText(Path.Combine(wtGit, "commondir"), "../..\n");
            Check("HEAD of a worktree reads the shared refs", GitHead.Read(wt) == sha2, GitHead.Read(wt));
            File.WriteAllText(Path.Combine(wtGit, "HEAD"), sha1 + "\n");
            Check("a detached HEAD is its own sha", GitHead.Read(wt) == sha1);
            File.WriteAllText(Path.Combine(wtGit, "HEAD"), "ref: refs/../../escape\n");
            Check("a ref that climbs out is refused", GitHead.Read(wt) == null);
            Check("no .git is unknown, not an error", GitHead.Read(Path.Combine(root, "nowhere")) == null && GitHead.Read(null) == null);

            Check("containment: a relative path inside the root resolves", CodeFiles.Inside(repo, "src/A.cs") == Path.Combine(repo, "src", "A.cs"));
            Check("containment: climbing out is refused", CodeFiles.Inside(repo, "../repo2/secret.txt") == null
                && CodeFiles.Inside(repo, "src/../../x") == null);
            Check("containment: rooted and drive paths are refused", CodeFiles.Inside(repo, Path.Combine(root, "x.cs")) == null
                && CodeFiles.Inside(repo, "C:x.cs") == null && CodeFiles.Inside(repo, "/etc/passwd") == null);

            Directory.CreateDirectory(Path.Combine(repo, "src"));
            var a = Path.Combine(repo, "src", "A.cs");
            File.WriteAllText(a, "class A {}");
            var graph = new CodeGraph { Repo = "r", RepoRoot = repo };
            graph.Files["src/A.cs"] = CodeFiles.Stamp(a)!;
            Check("freshness: an untouched file is unchanged", CodeFiles.Freshness(graph, "src/A.cs") == "unchanged");
            File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(5));
            Check("freshness: a touched but identical file is unchanged", CodeFiles.Freshness(graph, "src/A.cs") == "unchanged");
            File.WriteAllText(a, "class A { void B() {} }");
            Check("freshness: an edited file is changed", CodeFiles.Freshness(graph, "src/A.cs") == "changed");
            File.Delete(a);
            Check("freshness: a deleted file is missing", CodeFiles.Freshness(graph, "src/A.cs") == "missing");
            Check("freshness: a file never stamped is unverifiable", CodeFiles.Freshness(graph, "src/Other.cs") == "unverifiable");
            graph.RepoRoot = Path.Combine(root, "moved-away");
            graph.Files["src/B.cs"] = new CodeFileStamp { Sha256 = "x" };
            Check("freshness: a vanished checkout is unverifiable, not missing", CodeFiles.Freshness(graph, "src/B.cs") == "unverifiable");

            Check("repo slug: path characters are refused", Throws(() => CodeGraphStore.NormaliseRepo("../x"))
                && Throws(() => CodeGraphStore.NormaliseRepo("a/b")) && Throws(() => CodeGraphStore.NormaliseRepo("")));
            Check("repo slug: case folds", CodeGraphStore.NormaliseRepo(" BrainX ") == "brainx");

            var vault = Path.Combine(root, "vault");
            CodeGraphStore.Save(vault, new CodeGraph { Repo = "brainx", Commit = sha1 });
            Check("store: save/load round-trips", CodeGraphStore.Load(vault, "brainx")?.Commit == sha1
                && CodeGraphStore.List(vault).SequenceEqual(["brainx"]));
            Check("store: no scratch file is left behind", Directory.GetFiles(CodeGraphStore.Directory(vault)).Length == 1);
            File.WriteAllText(CodeGraphStore.PathFor(vault, "old"), JsonConvert.SerializeObject(new { Schema = "brainx-code-graph/v0" }));
            Check("store: another schema is refused, not half-read", Throws(() => CodeGraphStore.Load(vault, "old")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return Task.CompletedTask;

        static bool Throws(Action a) { try { a(); return false; } catch { return true; } }
    }

    /// <summary>
    /// A small hand-built graph: main → run → helper/log, Base ← Derived,
    /// a hub called from 60 places, and one INFERRED call that must sort
    /// after the EXTRACTED ones.
    /// </summary>
    private static CodeGraph TraceFixture()
    {
        CodeSymbol S(string name, string file, int line, string kind = "function") =>
            new() { Id = $"r:{file}#{name}@L{line}", Name = name, File = file, Line = line, Kind = kind, ExternalId = name };
        var main = S("main", "app.py", 1);
        var run = S("run", "app.py", 10);
        var helper = S("helper", "lib.py", 3);
        var log = S("log", "lib.py", 20);
        var hub = S("hub", "util.py", 1);
        var b = S("Base", "model.py", 1, "class");
        var d = S("Derived", "model.py", 9, "class");
        var g = new CodeGraph { Repo = "r", Symbols = [main, run, helper, log, hub, b, d] };
        CodeEdge E(CodeSymbol from, CodeSymbol to, string rel, string prov = "EXTRACTED", int line = 1) =>
            new() { From = from.Id, To = to.Id, Relation = rel, Provenance = prov, File = from.File, Line = line };
        g.Edges.AddRange([E(main, run, "calls", line: 2), E(run, log, "calls", "INFERRED", 11), E(run, helper, "calls", line: 12),
                          E(d, b, "inherits", line: 9), E(run, hub, "calls", line: 13)]);
        for (int i = 0; i < 60; i++)
        {
            var caller = S("caller" + i, "many.py", i + 1);
            g.Symbols.Add(caller);
            g.Edges.Add(E(caller, hub, "calls", line: i + 1));
        }
        return g;
    }

    private static Task CodeGraphTraceChecks()
    {
        var g = TraceFixture();
        CodeSymbol One(string name) => CodeGraphQuery.Resolve(g, name).Symbols.Single();
        var run = One("run");

        var callees = CodeGraphQuery.Trace(g, [run], new CodeTraceOptions { Direction = "out" });
        Check("out: callees only, EXTRACTED before INFERRED",
            callees.Edges.Select(e => g.Symbols.First(s => s.Id == e.Edge.To).Name).SequenceEqual(["helper", "hub", "log"]),
            string.Join(",", callees.Edges.Select(e => e.Edge.To)));
        var callers = CodeGraphQuery.Trace(g, [run], new CodeTraceOptions { Direction = "in" });
        Check("in: the caller only", callers.Edges.Count == 1 && callers.Edges[0].Edge.From.Contains("#main@"));
        Check("direction is caller → callee in every edge", callees.Edges.All(e => e.Edge.From == run.Id));

        var bases = CodeGraphQuery.Trace(g, [One("Derived")], new CodeTraceOptions { Direction = "out", Relations = ["inherits"] });
        Check("relation filter: inherits reaches the base class", bases.Edges.Count == 1 && bases.Nodes.Any(n => n.Name == "Base"));
        var none = CodeGraphQuery.Trace(g, [One("Derived")], new CodeTraceOptions { Relations = ["calls"] });
        Check("relation filter: no calls from a class", none.Edges.Count == 0 && none.TruncatedBy == null);

        var two = CodeGraphQuery.Trace(g, [One("main")], new CodeTraceOptions { Direction = "out", MaxHops = 2 });
        Check("hops: two hops reach the callees' callees, hop numbers recorded",
            two.Edges.Count(e => e.Hop == 1) == 1 && two.Edges.Count(e => e.Hop == 2) == 3, string.Join(",", two.Edges.Select(e => e.Hop)));
        var both = CodeGraphQuery.Trace(g, [One("main")], new CodeTraceOptions { MaxHops = 3, MaxEdges = 500, MaxTokens = 100000 });
        Check("hubs: reached at hop 2 but not expanded into its 60 callers",
            both.HubsNotExpanded.Any(h => h.Name == "hub") && both.Edges.All(e => !e.Edge.From.Contains("many.py")),
            $"{both.Edges.Count} edges");
        var hubSeed = CodeGraphQuery.Trace(g, [One("hub")], new CodeTraceOptions { Direction = "in", MaxEdges = 500, MaxTokens = 100000 });
        Check("hubs: a hub that is the seed still answers its first hop", hubSeed.Edges.Count == 61);

        var capped = CodeGraphQuery.Trace(g, [One("hub")], new CodeTraceOptions { Direction = "in", MaxEdges = 7, MaxTokens = 100000 });
        Check("max_edges cuts and says so", capped.Edges.Count == 7 && capped.TruncatedBy == "edges" && capped.EdgesConsidered == 61);
        var tight = CodeGraphQuery.Trace(g, [One("hub")], new CodeTraceOptions { Direction = "in", MaxEdges = 500, MaxTokens = 200 });
        Check("max_tokens cuts and says so", tight.TruncatedBy == "tokens" && tight.EstimatedTokens <= 200 && tight.Edges.Count > 0,
            $"{tight.Edges.Count} edges, {tight.EstimatedTokens} tokens");
        var again = CodeGraphQuery.Trace(g, [One("hub")], new CodeTraceOptions { Direction = "in", MaxEdges = 7, MaxTokens = 100000 });
        Check("the same question returns the same slice", again.Edges.Select(e => e.Edge.From).SequenceEqual(capped.Edges.Select(e => e.Edge.From)));

        g.Symbols.Add(new CodeSymbol { Id = "r:other.py#run@L5", Name = "run", File = "other.py", Line = 5, ExternalId = "run2" });
        var amb = CodeGraphQuery.Resolve(g, "run");
        Check("resolve: a repeated name is ambiguous with every candidate", amb.Status == "ambiguous" && amb.Symbols.Count == 2);
        Check("resolve: path#name picks one", CodeGraphQuery.Resolve(g, "other.py#run").Status == "exact"
            && CodeGraphQuery.Resolve(g, ".\\app.py#run").Symbols.Single().File == "app.py");
        Check("resolve: an id is exact", CodeGraphQuery.Resolve(g, run.Id).Symbols.Single() == run);
        Check("resolve: case-sensitive first, ignore-case only as a fallback",
            CodeGraphQuery.Resolve(g, "derived").MatchedBy == "name-ignore-case" && CodeGraphQuery.Resolve(g, "Derived").MatchedBy == "name");
        Check("resolve: unknown is not-found", CodeGraphQuery.Resolve(g, "nothing_here").Status == "not-found");
        return Task.CompletedTask;
    }
}
