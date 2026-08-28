using System.Text;
using System.Text.RegularExpressions;
using BrainX.Core.Models;
using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

/// <summary>
/// `brainx-mcp push-pack` — build the PUSH layer's lookup artifacts.
///
/// Everything the brain does today is pull-based: the agent must think to
/// search. Measured across 204 sessions (2026-08-28 note "วัดต้นทุนของการลืม"),
/// 13.9% of all weighted tokens went to re-acquiring context the vault already
/// held, and 34% of file reads were files an earlier session had already read.
/// The push layer attacks that from the other side: when the agent is ABOUT to
/// touch a file (or has just hit an error) that the vault holds a lesson
/// about, the hook warns without being asked.
///
/// Hooks run on the critical path of every tool call, so they never parse the
/// whole index. The two hot lookups are TSV files — one line per key — that a
/// PowerShell hook greps with Select-String and JSON-parses ONE line of.
/// Whole-file JSON parses of a multi-MB index in PS 5.1 cost seconds; a line
/// scan costs tens of milliseconds. repos.json is parsed whole, but only at
/// SessionStart, which is not a hot path.
///
/// Outputs under &lt;vault&gt;/.obsidianx/push-pack/:
///   files.tsv   basename → notes mentioning that file (warn-flagged, ranked)
///   errors.tsv  error fingerprint (code / exception name) → notes carrying it
///   repos.json  per-project warm-start pack (handoffs, gotchas, hot files)
///   meta.json   counts + generatedAt, so staleness is visible
///
/// Exact-match by design: file paths and error codes are deterministic keys.
/// The paraphrase benchmark (hit@5 39-46%) is the measured ceiling of the
/// semantic path; this layer avoids that ceiling instead of raising it.
/// </summary>
internal static partial class Program
{
    // ── mention extraction ────────────────────────────────────────────────

    // A path-ish token ending in a source-file extension. Deliberately NOT
    // .md: vault notes reference each other constantly and editing a note is
    // not the situation the warn layer exists for.
    private static readonly Regex FileMentionRx = new(
        @"(?<![\w/\\.-])((?:[A-Za-z0-9_.\-]+[/\\])*[A-Za-z0-9_.\-]+\.(?:cs|axaml|xaml|ps1|psm1|ts|tsx|js|jsx|mjs|py|php|json|yml|yaml|sh|bash|sql|dart|java|kt|go|rs|html|css|scss|vue|csproj|sln|slnx|toml|ini|conf|env))(?![\w.])",
        RegexOptions.Compiled);

    // Library files that appear in notes as TECHNOLOGY names, not as files of
    // any project. A bare mention of these is noise; a mention WITH a path
    // segment (public/js/chart.js) is a real project file and stays.
    private static readonly HashSet<string> LibraryBasenames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node.js", "chart.js", "alpine.js", "jquery.js", "three.js",
        "vue.js", "react.js", "angular.js", "next.js", "express.js",
        "moment.js", "bootstrap.css",
    };

    // Compiler/runtime error codes: CS1061, MSB3027, NU1605, NETSDK1045,
    // TS2345… + POSIX/node constants (ECONNREFUSED, ENOENT) + HRESULTs +
    // GitHub security advisories. These are the strongest fingerprints a
    // failure can carry — globally unique, survive rewording, and identical
    // in the note that recorded the fix and the terminal that just failed.
    private static readonly Regex ErrorCodeRx = new(
        @"\b(?:CS|MSB|NU|NETSDK|CA|SA|IDE|TS|RZ|BC|FS|AL)\d{3,5}\b|\bE[A-Z]{4,15}\b|\b0x8[0-9A-Fa-f]{7}\b|\bGHSA-[a-z0-9]{4}-[a-z0-9]{4}-[a-z0-9]{4}\b",
        RegexOptions.Compiled);

    private static readonly Regex ExceptionNameRx = new(
        @"\b[A-Z][A-Za-z0-9_]{2,60}(?:Exception|Error)\b",
        RegexOptions.Compiled);

    // Names that end in Exception/Error but discriminate nothing.
    private static readonly HashSet<string> GenericExceptionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Exception", "Error", "SystemException", "AggregateException",
        "InnerException", "TargetInvocationException", "UnhandledException",
        "FatalError", "StandardError", "InternalError",
    };

    // ── warn classification ───────────────────────────────────────────────

    // Ordered by severity: the first tag a note carries becomes its label in
    // the hook's warning line. `coding-lesson` is last on purpose — it is the
    // broadest tag in the vault and should never outrank a real gotcha.
    private static readonly string[] WarnTagPriority =
    {
        "gotcha", "bug", "bug-fix", "bugfix", "regression", "deadlock",
        "security", "incident", "data-loss", "playbook", "coding-lesson",
    };

    // Tags that name a PROJECT (repo packs group by these). Generic
    // status/tech tags never become pack keys.
    private static readonly HashSet<string> PackTagBlocklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "session-handoff", "shipped", "pending", "deployed", "in-progress",
        "coding-lesson", "gotcha", "playbook", "reusable", "measurement",
        "eval", "benchmark", "retrieval-benchmark", "csharp", "wpf", "mcp",
        "hooks", "php", "laravel", "sql", "docker", "windows", "dotnet",
        "dotnet-stack", "api", "research", "plan", "roadmap", "spec",
        "imported", "knowledge", "instructions", "thai", "bias", "gemma3",
        "embeddings", "onnx", "ollama", "semantic-search", "token-economy",
        "calibration", "bug", "bug-fix", "security", "kpi-progress",
    };

    private static readonly Regex DateTagRx = new(@"^\d{4}(-\d{2}){0,2}$", RegexOptions.Compiled);
    private static readonly Regex HostSegmentRx = new(@"\w\.(?:com|net|org|io|co|dev|app)/", RegexOptions.Compiled);

    internal static int PushPackCli(string[] args)
    {
        string? vaultArg = null;
        var quiet = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vault" && i + 1 < args.Length) vaultArg = args[++i];
            else if (args[i] == "--quiet") quiet = true;
            else if (args[i] is "-h" or "--help" or "help")
            {
                Console.WriteLine("Usage: brainx-mcp push-pack [--vault PATH] [--quiet]");
                Console.WriteLine();
                Console.WriteLine("Builds .obsidianx/push-pack/ — the exact-match lookup tables the");
                Console.WriteLine("proactive hooks read: file→lessons, error-fingerprint→fixes, and");
                Console.WriteLine("per-project warm-start packs. Also runs as part of `garden`.");
                return 0;
            }
        }
        if (!string.IsNullOrWhiteSpace(vaultArg) && Directory.Exists(vaultArg))
            _vaultPath = Path.GetFullPath(vaultArg);
        else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BRAINX_VAULT")))
            _vaultPath = Path.GetFullPath(Environment.GetEnvironmentVariable("BRAINX_VAULT")!);

        void Say(string s) { if (!quiet) Console.WriteLine(s); }
        Say($"brainx-mcp push-pack · v{ServerVersion}");
        Say($"  vault: {_vaultPath}");
        try
        {
            return BuildPushPack(_vaultPath, Say);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"push-pack failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Core builder — also called from the nightly garden pass so
    /// the pack can never silently go stale while the gardener runs.</summary>
    internal static int BuildPushPack(string vaultPath, Action<string>? say = null)
    {
        void Say(string s) => say?.Invoke(s);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var graph = new KnowledgeIndexer().IndexVault(vaultPath);

        // Per-basename and per-fingerprint entry lists. An entry is one note
        // that mentions the key; `p` keeps the longest path form the note
        // wrote so the hook can score how specifically it matches.
        var files = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
        var errors = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
        // tag → notes, for repo packs.
        var byTag = new Dictionary<string, List<KnowledgeNode>>(StringComparer.OrdinalIgnoreCase);
        // tag → basename → distinct note count, for per-repo hot files.
        var tagFiles = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        int readFailures = 0;
        foreach (var node in graph.Nodes)
        {
            string content;
            try { content = File.ReadAllText(node.FilePath); }
            catch { readFailures++; continue; }

            var rel = Path.GetRelativePath(vaultPath, node.FilePath).Replace('\\', '/');
            var warnTag = WarnTagPriority.FirstOrDefault(t =>
                node.Tags.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase)));
            if (warnTag == null && rel.Contains("/Bugs/", StringComparison.OrdinalIgnoreCase)) warnTag = "bug";
            if (warnTag == null && node.Kind == NoteKind.Playbook) warnTag = "playbook";

            JObject Entry(string written) => new()
            {
                ["p"] = written,
                ["n"] = node.Title,
                ["id"] = node.Id,
                ["s"] = node.Scope,
                ["w"] = warnTag != null,
                ["t"] = warnTag,
                ["m"] = node.ModifiedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            };

            // file mentions — longest written form per basename per note
            var longest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in FileMentionRx.Matches(content))
            {
                var written = m.Groups[1].Value.Replace('\\', '/');
                if (HostSegmentRx.IsMatch(written)) continue;               // cdn.example.com/chart.js
                var baseName = written[(written.LastIndexOf('/') + 1)..];
                if (baseName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                if (LibraryBasenames.Contains(baseName) && !written.Contains('/')) continue;
                if (!longest.TryGetValue(baseName, out var prev) || written.Length > prev.Length)
                    longest[baseName] = written;
            }
            foreach (var (baseName, written) in longest)
            {
                if (!files.TryGetValue(baseName, out var list)) files[baseName] = list = new();
                list.Add(Entry(written));
            }

            // error fingerprints
            var fps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in ErrorCodeRx.Matches(content)) fps.Add(m.Value);
            foreach (Match m in ExceptionNameRx.Matches(content))
                if (!GenericExceptionNames.Contains(m.Value)) fps.Add(m.Value);
            foreach (var fp in fps)
            {
                if (!errors.TryGetValue(fp, out var list)) errors[fp] = list = new();
                list.Add(Entry(fp));
            }

            // repo-pack grouping
            foreach (var tag in node.Tags)
            {
                if (PackTagBlocklist.Contains(tag) || DateTagRx.IsMatch(tag)) continue;
                if (!Regex.IsMatch(tag, "^[a-z][a-z0-9-]{2,30}$")) continue;
                if (!byTag.TryGetValue(tag, out var list)) byTag[tag] = list = new();
                list.Add(node);
                if (!tagFiles.TryGetValue(tag, out var tf)) tagFiles[tag] = tf = new(StringComparer.OrdinalIgnoreCase);
                foreach (var baseName in longest.Keys)
                    tf[baseName] = tf.TryGetValue(baseName, out var c) ? c + 1 : 1;
            }
        }

        // Rank + cap. Warn notes first, then newest; a hook reads the head of
        // the list, so the cap trades nothing it would ever use.
        static void SortAndCap(Dictionary<string, List<JObject>> map, int cap)
        {
            foreach (var list in map.Values)
            {
                list.Sort((a, b) =>
                {
                    var w = ((bool)b["w"]!).CompareTo((bool)a["w"]!);
                    return w != 0 ? w : string.CompareOrdinal((string?)b["m"], (string?)a["m"]);
                });
                if (list.Count > cap) list.RemoveRange(cap, list.Count - cap);
            }
        }
        // A fingerprint carried by half the vault identifies nothing — drop
        // BEFORE capping, because after the cap every list is ≤8 and the
        // ubiquity of the key is no longer visible.
        foreach (var k in errors.Where(kv => kv.Value.Count > 40).Select(kv => kv.Key).ToList())
            errors.Remove(k);
        SortAndCap(files, cap: 12);
        SortAndCap(errors, cap: 8);

        // Repo packs: a tag is a project if it labels at least 2 session
        // notes — projects accumulate handoffs, technologies do not.
        var repos = new JObject();
        foreach (var (tag, nodes) in byTag)
        {
            var sessions = nodes.Where(n => n.Kind == NoteKind.Session
                || n.Tags.Any(t => t.Equals("session-handoff", StringComparison.OrdinalIgnoreCase))).ToList();
            if (sessions.Count < 2) continue;

            JObject Brief(KnowledgeNode n) => new()
            {
                ["t"] = n.Title, ["id"] = n.Id, ["m"] = n.ModifiedAt.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            };
            var gotchas = nodes
                .Where(n => WarnTagPriority.Take(9).Any(t =>
                    n.Tags.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(n => n.ModifiedAt).Take(5).Select(Brief);
            var handoffs = sessions.OrderByDescending(n => n.ModifiedAt).Take(3).Select(Brief);
            var hot = tagFiles.TryGetValue(tag, out var tf)
                ? tf.OrderByDescending(kv => kv.Value).Take(8).Select(kv => kv.Key)
                : Enumerable.Empty<string>();

            repos[tag.ToLowerInvariant()] = new JObject
            {
                ["notes"] = nodes.Count,
                ["handoffs"] = new JArray(handoffs),
                ["gotchas"] = new JArray(gotchas),
                ["hot"] = new JArray(hot),
            };
        }

        // ── write ─────────────────────────────────────────────────────────
        var dir = Path.Combine(vaultPath, ".obsidianx", "push-pack");
        Directory.CreateDirectory(dir);
        var noBom = new UTF8Encoding(false);

        static void WriteTsv(string path, Dictionary<string, List<JObject>> map, Encoding enc)
        {
            var sb = new StringBuilder();
            foreach (var (key, list) in map.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(key.ToLowerInvariant()).Append('\t')
                  .Append(new JArray(list).ToString(Newtonsoft.Json.Formatting.None)).Append('\n');
            File.WriteAllText(path, sb.ToString(), enc);
        }
        WriteTsv(Path.Combine(dir, "files.tsv"), files, noBom);
        WriteTsv(Path.Combine(dir, "errors.tsv"), errors, noBom);
        File.WriteAllText(Path.Combine(dir, "repos.json"),
            repos.ToString(Newtonsoft.Json.Formatting.Indented), noBom);
        File.WriteAllText(Path.Combine(dir, "meta.json"), new JObject
        {
            ["schema"] = "push-pack/v1",
            ["generatedAt"] = DateTime.UtcNow.ToString("o"),
            ["notes"] = graph.Nodes.Count,
            ["readFailures"] = readFailures,
            ["fileKeys"] = files.Count,
            ["errorKeys"] = errors.Count,
            ["repoPacks"] = ((IDictionary<string, JToken?>)repos).Count,
        }.ToString(Newtonsoft.Json.Formatting.Indented), noBom);

        sw.Stop();
        Say($"  push-pack:  {files.Count} file key(s), {errors.Count} error fingerprint(s), "
          + $"{((IDictionary<string, JToken?>)repos).Count} repo pack(s) in {sw.ElapsedMilliseconds:n0} ms"
          + (readFailures > 0 ? $" ({readFailures} unreadable note(s) skipped)" : ""));
        Say($"  wrote       {dir}");
        return 0;
    }

}
