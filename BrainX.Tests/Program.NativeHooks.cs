using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using BrainX.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

// The hooks that fire on every tool call and prompt moved from PowerShell into
// brainx-mcp (BrainX.Mcp/NativeHooks.cs) because powershell.exe alone took 3 to
// 5.5 s to start on the owner's machine, per hook. Those hooks are how a session
// gets a file's past lessons and error recall, so the move only counts if nothing
// changed. These checks run the REAL script and the REAL binary on the same input
// in twin sandboxes and compare what came out: stdout, journals, cooldown files.
internal static partial class Program
{
    private static void RegisterNativeHookChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("native hooks: every script and its port say and write the same thing", NativeHooksMatchTheScripts));
        checks.Add(("native hooks: a bad payload is silence and exit 0, never a broken hook", NativeHooksFailQuietly));
        checks.Add(("native hooks: parallel writers lose no journal row, and Thai survives stdin", NativeHooksShareTheJournal));
        checks.Add(("native hooks: a Bash-style trap and a stale client cannot take them", NativeHookNamesAreSafe));
        checks.Add(("install-hooks: an old install upgrades to exec form and nothing else moves", InstallHooksUpgrades));
        checks.Add(("hook health: a native hook is judged by its binary and its place", HookHealthKnowsNativeHooks));
    }

    private sealed record HookRun(int Exit, string Stdout, string Stderr);

    private static async Task<HookRun> RunProcess(ProcessStartInfo psi, string stdin, string home, string vault)
    {
        psi.UseShellExecute = false;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = new UTF8Encoding(false);
        psi.Environment["USERPROFILE"] = home;
        psi.Environment["BRAINX_VAULT"] = vault;
        psi.Environment["BRAINX_SANDBOX"] = "1";
        using var p = Process.Start(psi)!;
        var utf8 = new UTF8Encoding(false).GetBytes(stdin);
        await p.StandardInput.BaseStream.WriteAsync(utf8);
        await p.StandardInput.BaseStream.FlushAsync();
        p.StandardInput.Close();
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) { try { p.Kill(true); } catch { } return new HookRun(-1, await so, await se); }
        return new HookRun(p.ExitCode, await so, await se);
    }

    private static Task<HookRun> RunScript(string script, string stdin, string home, string vault, string? workDir = null)
    {
        var psi = new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"");
        if (workDir != null) psi.WorkingDirectory = workDir;
        return RunProcess(psi, stdin, home, vault);
    }

    private static Task<HookRun> RunNative(string exe, string hook, string stdin, string home, string vault, string? workDir = null, params string[] extra)
    {
        var psi = new ProcessStartInfo(exe);
        psi.ArgumentList.Add(hook);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        if (workDir != null) psi.WorkingDirectory = workDir;
        return RunProcess(psi, stdin, home, vault);
    }

    /// <summary>One world for one hook run. Built twice per scenario, identically,
    /// so the script and the port each start from the same files.</summary>
    private sealed record HookWorld(string Root, string Home, string Claude, string Vault, string Repo);

    private static HookWorld MakeWorld(string root, string name)
    {
        var home = Path.Combine(root, name, "home");
        var claude = Path.Combine(home, ".claude");
        var vault = Path.Combine(root, name, "vault");
        var repo = Path.Combine(root, name, "repo");
        Directory.CreateDirectory(Path.Combine(claude, "scripts"));
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx", "push-pack"));
        Directory.CreateDirectory(repo);
        var aliases = FindHookScript("brain-aliases.json");
        if (aliases != null) File.Copy(aliases, Path.Combine(claude, "scripts", "brain-aliases.json"), true);

        string Lesson(string p, string n, string id, string? s, bool w, string? t, string m) =>
            new JObject { ["p"] = p, ["n"] = n, ["id"] = id, ["s"] = s, ["w"] = w, ["t"] = t, ["m"] = m }.ToString(Formatting.None);
        var fresh = DateTime.Now.AddDays(-5).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var aged = DateTime.Now.AddDays(-100).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllLines(Path.Combine(vault, ".obsidianx", "push-pack", "files.tsv"), new[]
        {
            "aaa.cs\t[" + Lesson("Src/aaa.cs", "broad note", "a00000000001", null, true, "coding-lesson", aged) + "]",
            "sharp.cs\t[" + Lesson("Src/Sharp/sharp.cs", "โน้ตไทย it's <sharp>", "b00000000001", "demo", true, "gotcha", fresh)
                + "," + Lesson("Other/sharp.cs", "second", "b00000000002", null, true, "bug", aged)
                + "," + Lesson("Other/sharp.cs", "not a warning", "b00000000003", null, false, "bug", aged) + "]",
            "ctx.cs\t[" + Lesson("ctx.cs", "ctx note", "c00000000001", null, true, "gotcha", fresh) + "]",
            "quiet.cs\t[" + Lesson("quiet.cs", "not flagged", "d00000000001", null, false, "gotcha", fresh) + "]",
        }, new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(vault, ".obsidianx", "push-pack", "errors.tsv"), new[]
        {
            "cs1061\t[" + Lesson("CS1061", "missing member", "e00000000001", null, false, null, aged) + "]",
            "enoent\t[" + Lesson("ENOENT", "file gone ไทย", "e00000000002", null, false, null, fresh) + "]",
            "nullreferenceexception\t[" + Lesson("NullReferenceException", "null trap", "e00000000001", null, false, null, aged)
                + "," + Lesson("NullReferenceException", "null trap 2", "e00000000003", null, false, null, aged) + "]",
        }, new UTF8Encoding(false));
        return new HookWorld(root, home, claude, vault, repo);
    }

    private static string Stamp(int minutesAgo) => DateTimeOffset.Now.AddMinutes(-minutesAgo).ToString("o");

    private static string ToolLogRow(string tool, int minutesAgo) =>
        new JObject { ["ts"] = Stamp(minutesAgo), ["tool"] = tool, ["mode"] = "always", ["resp_size"] = 1, ["resp_nonascii"] = 0 }.ToString(Formatting.None);

    private static string SearchRow(string query, int hits, int minutesAgo) =>
        new JObject { ["ts"] = Stamp(minutesAgo), ["query"] = query, ["hits"] = hits, ["size"] = 10 }.ToString(Formatting.None);

    private static string TranscriptLine(int minutesAgo, params string[] tools) => new JObject
    {
        ["type"] = "assistant",
        ["timestamp"] = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
        ["message"] = new JObject
        {
            ["role"] = "assistant",
            ["content"] = new JArray(tools.Select((t, i) => (object)new JObject { ["type"] = "tool_use", ["id"] = "t" + i, ["name"] = t, ["input"] = new JObject() }).ToArray()),
        },
    }.ToString(Formatting.None);

    /// <summary>What a run left behind, with every timestamp masked and the
    /// floating audit age rounded: the two runs happen seconds apart.</summary>
    private static string Evidence(HookWorld w, string stdout, params string[] extraFiles)
    {
        var sb = new StringBuilder();
        sb.Append("exit-independent stdout: ").AppendLine(Mask(stdout, w));
        foreach (var f in new[] { "tool-log.ndjson", "brain-search-log.ndjson", "brain-decisions.ndjson", "brain-stop-log.ndjson" })
        {
            var p = Path.Combine(w.Claude, f);
            if (!File.Exists(p)) continue;
            var bytes = File.ReadAllBytes(p);
            sb.Append(f).Append(" bom=").Append(bytes.Length >= 3 && bytes[0] == 0xEF).Append(": ");
            sb.AppendLine(Mask(new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "⏎"), w));
        }
        var cache = Path.Combine(w.Claude, "cache");
        if (Directory.Exists(cache))
            foreach (var f in Directory.GetFiles(cache).OrderBy(x => x, StringComparer.Ordinal))
                sb.Append(Path.GetFileName(f)).Append(": ").AppendLine(File.ReadAllText(f).Replace("\r\n", "⏎"));
        var activity = Path.Combine(w.Vault, ".obsidianx", "agent-bus", "activity", "claude-code.ndjson");
        if (File.Exists(activity)) sb.Append("activity: ").AppendLine(Mask(File.ReadAllText(activity).Replace("\r\n", "⏎"), w));
        foreach (var f in extraFiles)
            sb.Append(Path.GetFileName(f)).Append(": ").AppendLine(File.Exists(f) ? Mask(File.ReadAllText(f), w) : "(none)");
        return sb.ToString();
    }

    private static string Mask(string s, HookWorld w)
    {
        s = Regex.Replace(s, @"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?(Z|[+-]\d\d:\d\d)?", "TS");
        s = Regex.Replace(s, @"""auditAge"":[0-9.eE+-]+", "\"auditAge\":N");
        return s.Replace(w.Vault, "VAULT").Replace(w.Vault.Replace("\\", "\\\\"), "VAULT")
                .Replace(w.Repo, "REPO").Replace(w.Repo.Replace("\\", "\\\\"), "REPO");
    }

    private sealed record HookCase(string Name, string Script, string Hook, Func<HookWorld, string> Stdin,
                                   Action<HookWorld>? Setup = null, string[]? Files = null, bool InRepo = false, string[]? Extra = null);

    private static List<HookCase> HookCases()
    {
        static string Payload(JObject o) => o.ToString(Formatting.None);
        static JObject Tool(string tool, JToken response, JObject? input = null, string? sid = "s1") => new()
        {
            ["session_id"] = sid, ["hook_event_name"] = "PostToolUse", ["tool_name"] = tool,
            ["tool_input"] = input ?? new JObject { ["command"] = "x" }, ["tool_response"] = response,
        };
        static JObject Edit(string file, string cwd, string sid = "s1") => new()
        {
            ["session_id"] = sid, ["cwd"] = cwd, ["tool_name"] = "Edit", ["tool_input"] = new JObject { ["file_path"] = file },
        };
        static string Prompt(string p) => Payload(new JObject { ["prompt"] = p });
        var searchResponse = new JArray(new JObject { ["type"] = "text", ["text"] = "{\"count\":4,\"results\":[1,2,3,4]}" });
        const string longEnough = "how does the cowork room recover after a dropped claude run in this build";

        void Log(HookWorld w, string file, params string[] rows) =>
            File.WriteAllLines(Path.Combine(w.Claude, file), rows, new UTF8Encoding(true));

        var cases = new List<HookCase>
        {
            // ── the logger ──
            new("logger: object response with HTML-sensitive characters", "brain-tool-logger.ps1", "hook-tool-log",
                _ => Payload(Tool("Bash", new JObject { ["stdout"] = "it's <b>a</b> & ไทย", ["stderr"] = "", ["interrupted"] = false }))),
            new("logger: a one-element array is unrolled", "brain-tool-logger.ps1", "hook-tool-log",
                _ => Payload(Tool("mcp__x__y", new JArray(new JObject { ["type"] = "text", ["text"] = "สวัสดี" })))),
            new("logger: a several-element array stays an array", "brain-tool-logger.ps1", "hook-tool-log",
                _ => Payload(Tool("mcp__x__y", new JArray(new JObject { ["a"] = 1 }, new JObject { ["b"] = 2 })))),
            new("logger: a string response", "brain-tool-logger.ps1", "hook-tool-log",
                _ => Payload(Tool("Read", "plain ไทย text"))),
            new("logger: empty response", "brain-tool-logger.ps1", "hook-tool-log", _ => Payload(Tool("Read", ""))),
            new("logger: brain_search row, by suffix, with the real count", "brain-tool-logger.ps1", "hook-tool-log",
                _ => Payload(Tool("mcp__brainx-brain__brain_search", searchResponse, new JObject { ["query"] = "ค้นหา it's" }))),
            new("logger: brain_search with results and no count", "brain-tool-logger.ps1", "hook-tool-log",
                _ => Payload(Tool("mcp__any__brain_search", new JArray(new JObject { ["type"] = "text", ["text"] = "{\"results\":[1,2,3]}" }), new JObject { ["query"] = "q" }))),
            new("logger: brain_search with an object envelope", "brain-tool-logger.ps1", "hook-tool-log",
                _ => Payload(Tool("mcp__any__brain_search", new JObject { ["content"] = new JArray(new JObject { ["text"] = "{\"count\":0}" }) }, new JObject { ["query"] = "none" }))),
            new("logger: a mode file is logged", "brain-tool-logger.ps1", "hook-tool-log", _ => Payload(Tool("Bash", new JObject { ["stdout"] = "x" })),
                w => File.WriteAllText(Path.Combine(w.Claude, "brain-mode.txt"), "AUTO\r\n")),
            new("logger: an existing journal is appended to", "brain-tool-logger.ps1", "hook-tool-log", _ => Payload(Tool("Bash", new JObject { ["stdout"] = "x" })),
                w => Log(w, "tool-log.ndjson", ToolLogRow("Edit", 1))),
            new("logger: no tool name", "brain-tool-logger.ps1", "hook-tool-log", _ => "{\"tool_response\":{}}"),

            // ── the prompt gate ──
            new("gate: a normal prompt fires", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(longEnough)),
            new("gate: user_prompt is the fallback field", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Payload(new JObject { ["user_prompt"] = longEnough })),
            new("gate: mode off", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(longEnough),
                w => File.WriteAllText(Path.Combine(w.Claude, "brain-mode.txt"), "off")),
            new("gate: auto mode skips a short prompt", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt("short one"),
                w => File.WriteAllText(Path.Combine(w.Claude, "brain-mode.txt"), "auto")),
            new("gate: auto mode keeps a long one", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(longEnough + " and then some more words to pass eighty"),
                w => File.WriteAllText(Path.Combine(w.Claude, "brain-mode.txt"), "auto")),
            new("gate: long prompt", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(new string('x', 1501))),
            new("gate: code block", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt("look at this ```var x``` please")),
            new("gate: windows path", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(@"open D:\Code\thing and tell me why it failed")),
            new("gate: relative file", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt("why does ./src/app.tsx render twice on load")),
            new("gate: a word that only looks like a path", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt("why is the a/b test still running in production")),
            new("gate: Thai continuation", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt("ทำต่อเลยครับ")),
            new("gate: the longer word wins, it is listed first", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt("ไม่ใช่แบบนั้น")),
            new("gate: English continuation", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt("  ok do that")),
            new("gate: no continuation at 100 characters", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt("ok " + new string('y', 97))),
            new("gate: empty prompt", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt("")),
            new("gate: empty stdin", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => ""),
            new("gate: bad JSON", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => "{ not json"),
            new("gate: recent searches split hit and miss, oldest dropped, repeats folded", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(longEnough),
                w => Log(w, "brain-search-log.ndjson", new[] { SearchRow("old", 3, 90) }
                    .Concat(Enumerable.Range(1, 10).Select(i => SearchRow("hit" + i, i, 20)))
                    .Concat(new[] { SearchRow("hit3", 1, 19), SearchRow("พลาด", 0, 10), SearchRow("miss it's", 0, 9) }).ToArray())),
            new("gate: exactly one recent search", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(longEnough),
                w => Log(w, "brain-search-log.ndjson", SearchRow("only one", 2, 5))),
            new("gate: alias hints", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt("what happened to the CLAUDEX coder and the บอทดูดวง this time around")),
            new("gate: two edits and no brain read: stronger nudge", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(longEnough),
                w => { Log(w, "tool-log.ndjson", ToolLogRow("Edit", 3), ToolLogRow("Write", 2)); File.WriteAllText(Path.Combine(w.Claude, ".last-stop-marker"), Stamp(10) + "\r\n", new UTF8Encoding(true)); }),
            new("gate: exactly one edit", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(longEnough),
                w => Log(w, "tool-log.ndjson", ToolLogRow("Edit", 3))),
            new("gate: two edits and exactly one brain read", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(longEnough),
                w => Log(w, "tool-log.ndjson", ToolLogRow("mcp__brainx-brain__brain_recall", 4), ToolLogRow("Edit", 3), ToolLogRow("Edit", 2))),
            new("gate: two edits and two brain reads", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(longEnough),
                w => Log(w, "tool-log.ndjson", ToolLogRow("mcp__a__brain_walk", 4), ToolLogRow("mcp__b__brain_get_note", 4), ToolLogRow("Edit", 3), ToolLogRow("Edit", 2))),
            new("gate: edits before the last stop do not count", "brain-prompt-gate.ps1", "hook-prompt-gate", _ => Prompt(longEnough),
                w => { Log(w, "tool-log.ndjson", ToolLogRow("Edit", 30), ToolLogRow("Edit", 29)); File.WriteAllText(Path.Combine(w.Claude, ".last-stop-marker"), Stamp(10) + "\r\n", new UTF8Encoding(true)); }),

            // ── the Stop nudge ──
            new("stop: a quiet turn logs and says nothing", "stop-hook.ps1", "hook-brain-end",
                w => Payload(new JObject { ["transcript_path"] = Path.Combine(w.Root, Path.GetFileName(Path.GetDirectoryName(w.Home)!), "t.jsonl") }),
                w => File.WriteAllLines(Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl"), new[] { TranscriptLine(1, "Read") }), InRepo: true),
            new("stop: one edit, no save", "stop-hook.ps1", "hook-brain-end",
                w => Payload(new JObject { ["transcript_path"] = Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl") }),
                w => { File.WriteAllLines(Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl"), new[] { TranscriptLine(30, "Edit", "Edit", "Edit"), TranscriptLine(1, "Edit") });
                       File.WriteAllText(Path.Combine(w.Claude, ".last-stop-marker"), Stamp(5) + "\r\n", new UTF8Encoding(true)); }, InRepo: true),
            new("stop: three edits, no save, a stronger nudge", "stop-hook.ps1", "hook-brain-end",
                w => Payload(new JObject { ["transcript_path"] = Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl") }),
                w => File.WriteAllLines(Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl"), new[] { TranscriptLine(1, "Edit", "Write", "NotebookEdit") }), InRepo: true),
            new("stop: a save is acknowledged", "stop-hook.ps1", "hook-brain-end",
                w => Payload(new JObject { ["transcript_path"] = Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl") }),
                w => File.WriteAllLines(Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl"), new[] { TranscriptLine(1, "Edit", "mcp__brainx-brain__brain_remember") }), InRepo: true),
            new("stop: Task is substantive but is no edit", "stop-hook.ps1", "hook-brain-end",
                w => Payload(new JObject { ["transcript_path"] = Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl") }),
                w => File.WriteAllLines(Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl"), new[] { TranscriptLine(1, "Task") }), InRepo: true),
            new("stop: no transcript falls back to the shared tool log", "stop-hook.ps1", "hook-brain-end", _ => "{}",
                w => Log(w, "tool-log.ndjson", ToolLogRow("Edit", 2)), InRepo: true),
            new("stop: no transcript and a quiet log", "stop-hook.ps1", "hook-brain-end", _ => "{}", null, InRepo: true),
            new("stop: mode off", "stop-hook.ps1", "hook-brain-end", _ => "{}",
                w => File.WriteAllText(Path.Combine(w.Claude, "brain-mode.txt"), "off"), InRepo: true),
            new("stop: a fresh, healthy audit in the vault adds nothing", "stop-hook.ps1", "hook-brain-end",
                w => Payload(new JObject { ["transcript_path"] = Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl") }),
                w => { File.WriteAllLines(Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl"), new[] { TranscriptLine(1, "Edit") });
                       File.WriteAllText(Path.Combine(w.Vault, ".obsidianx", "last-audit.json"), new JObject { ["scannedAt"] = Stamp(60), ["brainHealth"] = 0.98 }.ToString()); }, InRepo: true),
            new("stop: an old audit found by walking up from the repo", "stop-hook.ps1", "hook-brain-end",
                w => Payload(new JObject { ["transcript_path"] = Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl") }),
                w => { File.WriteAllLines(Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl"), new[] { TranscriptLine(1, "Edit") });
                       Directory.CreateDirectory(Path.Combine(w.Repo, ".obsidianx"));
                       File.WriteAllText(Path.Combine(w.Repo, ".obsidianx", "last-audit.json"), new JObject { ["scannedAt"] = DateTimeOffset.Now.AddDays(-9.5).ToString("o"), ["brainHealth"] = 0.9 }.ToString()); }, InRepo: true),
            new("stop: an unhealthy brain", "stop-hook.ps1", "hook-brain-end",
                w => Payload(new JObject { ["transcript_path"] = Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl") }),
                w => { File.WriteAllLines(Path.Combine(Path.GetDirectoryName(w.Home)!, "t.jsonl"), new[] { TranscriptLine(1, "Edit") });
                       File.WriteAllText(Path.Combine(w.Vault, ".obsidianx", "last-audit.json"), new JObject { ["scannedAt"] = Stamp(60), ["brainHealth"] = 0.5555 }.ToString()); }, InRepo: true),

            // ── the file warning ──
            new("warn: a sharp, fresh lesson asks", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => Payload(Edit(@"D:\Code\Sharp\Sharp.cs", @"D:\Code\demo"))),
            new("warn: the middle tier goes to the agent", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => Payload(Edit(@"D:\Code\ctx.cs", @"D:\Code\other"))),
            new("warn: below the threshold is silence", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => Payload(Edit(@"D:\Code\aaa.cs", @"D:\Code\other"))),
            new("warn: an unflagged lesson is silence", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => Payload(Edit(@"D:\Code\quiet.cs", @"D:\Code\other"))),
            new("warn: an unknown file is silence", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => Payload(Edit(@"D:\Code\nothing.cs", @"D:\Code\other"))),
            new("warn: scratch folders are skipped", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => Payload(Edit(@"D:\Code\temp\Sharp.cs", @"D:\Code\demo"))),
            new("warn: markdown is skipped", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => Payload(Edit(@"D:\Code\sharp.md", @"D:\Code\demo"))),
            new("warn: the vault is skipped", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => Payload(Edit(Path.Combine(w.Vault, "Sharp.cs"), @"D:\Code\demo"))),
            new("warn: a file already warned about this session", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => Payload(Edit(@"D:\Code\Sharp\Sharp.cs", @"D:\Code\demo")),
                w => { Directory.CreateDirectory(Path.Combine(w.Claude, "cache")); File.WriteAllText(Path.Combine(w.Claude, "cache", "brainx-warned-s1.txt"), "sharp.cs\r\n"); }),
            new("warn: the session cap of three", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => Payload(Edit(@"D:\Code\Sharp\Sharp.cs", @"D:\Code\demo")),
                w => { Directory.CreateDirectory(Path.Combine(w.Claude, "cache")); File.WriteAllText(Path.Combine(w.Claude, "cache", "brainx-warned-s1.txt"), "a.cs\r\nb.cs\r\nc.cs\r\n"); }),
            new("warn: a Write counts, a Read does not", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => "{\"tool_name\":\"Read\",\"tool_input\":{\"file_path\":\"D:\\\\Code\\\\Sharp\\\\Sharp.cs\"}}"),
            new("warn: mode off", "brain-pretool-warn.ps1", "hook-pretool-warn",
                w => Payload(Edit(@"D:\Code\Sharp\Sharp.cs", @"D:\Code\demo")),
                w => File.WriteAllText(Path.Combine(w.Claude, "brain-mode.txt"), "off")),

            // ── error recall ──
            new("recall: a masked build failure", "brain-error-recall.ps1", "hook-error-recall",
                _ => Payload(Tool("Bash", new JObject { ["stdout"] = "Program.cs(3,1): error CS1061: no such member\nBuild FAILED.", ["stderr"] = "" }))),
            new("recall: a fingerprint on stderr alone", "brain-error-recall.ps1", "hook-error-recall",
                _ => Payload(Tool("PowerShell", new JObject { ["stdout"] = "fine", ["stderr"] = "open failed: ENOENT" }))),
            new("recall: source code on stdout is not a failure", "brain-error-recall.ps1", "hook-error-recall",
                _ => Payload(Tool("Bash", new JObject { ["stdout"] = "throw new NullReferenceException(); // CS1061", ["stderr"] = "" }))),
            new("recall: several tokens cite distinct notes", "brain-error-recall.ps1", "hook-error-recall",
                _ => Payload(Tool("Bash", new JObject { ["stdout"] = "", ["stderr"] = "fatal: CS1061 then NullReferenceException and ENOENT" }))),
            new("recall: a token already seen stays quiet", "brain-error-recall.ps1", "hook-error-recall",
                _ => Payload(Tool("Bash", new JObject { ["stdout"] = "", ["stderr"] = "fatal: ENOENT" })),
                w => { Directory.CreateDirectory(Path.Combine(w.Claude, "cache")); File.WriteAllText(Path.Combine(w.Claude, "cache", "brainx-errseen-s1.txt"), "enoent\r\n"); }),
            new("recall: an unknown token stays quiet", "brain-error-recall.ps1", "hook-error-recall",
                _ => Payload(Tool("Bash", new JObject { ["stdout"] = "", ["stderr"] = "fatal: CS9999" }))),
            new("recall: a string response is searched as JSON", "brain-error-recall.ps1", "hook-error-recall",
                _ => Payload(Tool("Bash", "Build FAILED with error CS1061 here"))),
            new("recall: only the last 8000 characters count", "brain-error-recall.ps1", "hook-error-recall",
                _ => Payload(Tool("Bash", new JObject { ["stdout"] = "Build FAILED CS1061\n" + new string('x', 9000), ["stderr"] = "" }))),
            new("recall: another tool is ignored", "brain-error-recall.ps1", "hook-error-recall",
                _ => Payload(Tool("Read", new JObject { ["stdout"] = "fatal: ENOENT" }))),
        };
        return cases;
    }

    private static async Task NativeHooksMatchTheScripts()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        if (!OperatingSystem.IsWindows()) return;
        if (FindHookScript("brain-tool-logger.ps1") == null) { Check("hooks/brain-tool-logger.ps1 found", false); return; }

        var root = TempDir("brainx-native-");
        try
        {
            var cases = HookCases();
            // The script takes 3 to 5 s to start on a loaded machine; four at a
            // time keeps the check to a couple of minutes.
            using var gate = new SemaphoreSlim(4);
            var results = await Task.WhenAll(cases.Select(async (c, i) =>
            {
                await gate.WaitAsync();
                try
                {
                    var a = MakeWorld(root, $"{i}s");
                    var b = MakeWorld(root, $"{i}n");
                    c.Setup?.Invoke(a);
                    c.Setup?.Invoke(b);
                    var script = FindHookScript(c.Script)!;
                    var patched = Path.Combine(a.Claude, "scripts", c.Script);
                    // As the installer stamps it, BOM included: 5.1 reads these as ANSI otherwise.
                    File.WriteAllText(patched, File.ReadAllText(script).Replace("__BRAINX_VAULT__", a.Vault), new UTF8Encoding(true));

                    var ps = await RunScript(patched, c.Stdin(a), a.Home, a.Vault, c.InRepo ? a.Repo : null);
                    var extra = (c.Extra ?? Array.Empty<string>()).Concat(new[] { "--vault", b.Vault }).ToArray();
                    var native = await RunNative(exe, c.Hook, c.Stdin(b), b.Home, b.Vault, c.InRepo ? b.Repo : null, extra);
                    return (c.Name, Ps: (ps.Exit, Evidence(a, ps.Stdout)), Native: (native.Exit, Evidence(b, native.Stdout)));
                }
                finally { gate.Release(); }
            }));

            foreach (var r in results)
                Check($"same output: {r.Name}", r.Ps.Item1 == r.Native.Item1 && r.Ps.Item2 == r.Native.Item2,
                      $"\n--- script (exit {r.Ps.Item1})\n{r.Ps.Item2}\n--- native (exit {r.Native.Item1})\n{r.Native.Item2}");

            // Two scripts that died on start would also compare equal, so the runs
            // that have something to say are held to saying it, script side included.
            foreach (var (name, says) in new[]
                     {
                         ("logger: a string response", "\"tool\":\"Read\""),
                         ("logger: brain_search row, by suffix, with the real count", "\"hits\":4"),
                         ("gate: a normal prompt fires", "UserPromptSubmit"),
                         ("gate: mode off", "\"reason\":\"mode-off\""),
                         ("gate: empty stdin", "\"reason\":\"empty-prompt\""),
                         ("gate: Thai continuation", "\"action\":\"skipped\""),
                         ("gate: two edits and no brain read: stronger nudge", "UserPromptSubmit"),
                         ("stop: a quiet turn logs and says nothing", "\"verdict\":\"quiet-turn\""),
                         ("stop: three edits, no save, a stronger nudge", "\"hookEventName\":\"Stop\""),
                         ("stop: a save is acknowledged", "\"verdict\":\"saved\""),
                         ("stop: an old audit found by walking up from the repo", "\"auditAge\":N"),
                         ("warn: a sharp, fresh lesson asks", "\"permissionDecision\":\"ask\""),
                         ("warn: the middle tier goes to the agent", "additionalContext"),
                         ("recall: a masked build failure", "cs1061: seen before"),
                         ("recall: several tokens cite distinct notes", "e00000000003"),
                     })
            {
                var r = results.Single(x => x.Name == name);
                Check($"not vacuous: {name}", r.Ps.Item2.Contains(says, StringComparison.Ordinal), r.Ps.Item2);
            }
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task NativeHooksFailQuietly()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var root = TempDir("brainx-native-quiet-");
        try
        {
            var w = MakeWorld(root, "w");
            foreach (var hook in new[] { "hook-tool-log", "hook-pretool-warn", "hook-error-recall", "hook-brain-end", "hook-auto-ingest", "hook-prompt-gate" })
                foreach (var (label, input) in new[] { ("nothing", ""), ("garbage", "{ not json"), ("a bare array", "[1,2]"), ("a tool_input of the wrong type", "{\"tool_name\":\"Edit\",\"tool_input\":3}") })
                {
                    var r = await RunNative(exe, hook, input, w.Home, w.Vault, null, "--vault", w.Vault);
                    // A Stop with no payload is a turn with no transcript, not a
                    // failure: it reads the tool log, which these runs have written to.
                    var mayNudge = hook == "hook-brain-end";
                    Check($"{hook} on {label}: exit 0, and nothing on stdout or stderr",
                          r.Exit == 0 && (mayNudge || r.Stdout.Length == 0) && r.Stderr.Length == 0, $"exit {r.Exit} [{r.Stdout}] [{r.Stderr}]");
                }

            // No ~/.claude at all, no vault: a fresh machine, or a wiped profile.
            var gone = Path.Combine(root, "gone");
            var sw = Stopwatch.StartNew();
            var r2 = await RunNative(exe, "hook-tool-log", "{\"tool_name\":\"Bash\"}", gone, Path.Combine(root, "no-vault"));
            sw.Stop();
            Check("no ~/.claude: still exit 0, and no retry loop on a folder that is not there",
                  r2.Exit == 0 && r2.Stderr.Length == 0 && sw.ElapsedMilliseconds < 800, $"exit {r2.Exit}, {sw.ElapsedMilliseconds} ms");

            // The rule the Stop nudge was given on 2026-08-28: a diagnostic run
            // must not move the state that defines "this turn".
            var marker = Path.Combine(w.Claude, ".last-stop-marker");
            var before = Stamp(5) + "\r\n";
            File.WriteAllText(marker, before, new UTF8Encoding(true));
            await RunNative(exe, "hook-brain-end", "{}", w.Home, w.Vault, null, "--no-marker", "--vault", w.Vault);
            Check("--no-marker leaves .last-stop-marker alone and marks the row a probe",
                  File.ReadAllText(marker) == before && File.ReadAllText(Path.Combine(w.Claude, "brain-stop-log.ndjson")).Contains("\"probe\":true"));
            await RunNative(exe, "hook-brain-end", "{}", w.Home, w.Vault, null, "--vault", w.Vault);
            Check("a real Stop moves it", File.ReadAllText(marker) != before);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task NativeHooksShareTheJournal()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var root = TempDir("brainx-native-share-");
        try
        {
            var w = MakeWorld(root, "w");
            const string thai = "คำถามภาษาไทย";
            var payload = new JObject
            {
                ["tool_name"] = "mcp__brainx-brain__brain_search",
                ["tool_input"] = new JObject { ["query"] = thai },
                ["tool_response"] = new JArray(new JObject { ["type"] = "text", ["text"] = "{\"count\":2}" }),
            }.ToString(Formatting.None);

            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => RunNative(exe, "hook-tool-log", payload, w.Home, w.Vault)));

            var rows = File.ReadAllLines(Path.Combine(w.Claude, "tool-log.ndjson"), Encoding.UTF8);
            var parsed = rows.Count(l => { try { return JObject.Parse(l)["tool"] != null; } catch { return false; } });
            Check("16 hooks at once leave 16 whole rows in the shared log", rows.Length == 16 && parsed == 16, $"{rows.Length} rows, {parsed} parse");
            var search = File.ReadAllLines(Path.Combine(w.Claude, "brain-search-log.ndjson"), Encoding.UTF8);
            Check("Thai arrives intact through raw stdin, whatever the console codepage is",
                  search.Length == 16 && search.All(l => (string?)JObject.Parse(l)["query"] == thai), search.FirstOrDefault());

            var hooks = new[] { "hook-tool-log", "hook-prompt-gate" };
            var log = File.ReadAllBytes(Path.Combine(w.Claude, "tool-log.ndjson"));
            Check("a new journal starts with a BOM, as Add-Content wrote it", log.Length > 3 && log[0] == 0xEF && log[1] == 0xBB && log[2] == 0xBF);
            Check("and has one BOM, however many writers raced to create it",
                  Encoding.UTF8.GetString(log).Skip(1).All(c => c != '\uFEFF'));

            var feed = Path.Combine(w.Vault, ".obsidianx", "agent-bus", "activity", "claude-code.ndjson");
            var edit = new JObject { ["tool_name"] = "Edit", ["tool_input"] = new JObject { ["file_path"] = @"D:\Code\ไทย.cs" } }.ToString(Formatting.None);
            var r = await RunNative(exe, "hook-auto-ingest", edit, w.Home, w.Vault, null, "--vault", w.Vault);
            var feedBytes = File.ReadAllBytes(feed);
            Check("the work feed gets the edit, with NO BOM: Program.Activity.cs drops a first line that has one",
                  r.Exit == 0 && feedBytes[0] == (byte)'{' && (string?)JObject.Parse(Encoding.UTF8.GetString(feedBytes))["summary"] == @"D:\Code\ไทย.cs");
            var read = new JObject { ["tool_name"] = "Read", ["tool_input"] = new JObject { ["file_path"] = @"D:\Code\b.cs" } }.ToString(Formatting.None);
            await RunNative(exe, "hook-auto-ingest", read, w.Home, w.Vault, null, "--vault", w.Vault);
            Check("a Read of a code file reports nothing", File.ReadAllLines(feed).Length == 1);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static Task NativeHookNamesAreSafe()
    {
        // BrainX.Client deletes any brainx-mcp command containing " hook-stop" or
        // " hook-session-start" as its own wake hook (UpsertWakeHook). A Stop
        // hook named after those would vanish on the app's next start.
        var names = new[] { "hook-tool-log", "hook-prompt-gate", "hook-brain-end", "hook-pretool-warn", "hook-error-recall", "hook-auto-ingest" };
        Check("no native hook name starts with what the client takes for a wake hook",
              names.All(n => !n.StartsWith("hook-stop", StringComparison.Ordinal) && !n.StartsWith("hook-session-start", StringComparison.Ordinal)));
        // The installed client looks for this substring, or it adds the script back.
        Check("the native auto-ingest tag still contains what an older client looks for",
              HookHealth.AutoIngestNativeTag.Contains("obsidianx-auto-ingest v5", StringComparison.Ordinal));
        return Task.CompletedTask;
    }

    private static async Task<HookRun> RunInstallHooks(string exe, string claudeDir, string vault, string? claudeVersion, params string[] extra)
    {
        var psi = new ProcessStartInfo(exe);
        psi.ArgumentList.Add("install-hooks");
        psi.ArgumentList.Add("--vault");
        psi.ArgumentList.Add(vault);
        foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.Environment["CLAUDE_CONFIG_DIR"] = claudeDir;
        psi.Environment["BRAINX_CLAUDE_VERSION"] = claudeVersion ?? "2.1.296 (Claude Code)";
        return await RunProcess(psi, "", Path.GetDirectoryName(claudeDir)!, vault);
    }

    private static JArray Handlers(JObject settings, string ev) =>
        new((settings["hooks"]?[ev] as JArray ?? new JArray()).SelectMany(g => g["hooks"] as JArray ?? new JArray()));

    private static async Task InstallHooksUpgrades()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var root = TempDir("brainx-install-hooks-");
        try
        {
            var claude = Path.Combine(root, "home", ".claude");
            var vault = Path.Combine(root, "My Vault");
            Directory.CreateDirectory(Path.Combine(claude, "scripts"));
            Directory.CreateDirectory(vault);
            var settings = Path.Combine(claude, "settings.json");
            var scripts = Path.Combine(claude, "scripts").Replace('\\', '/');

            string Ps(string s, string mark) => $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scripts}/{s}\" {mark}";
            JObject Entry(string cmd, string? matcher, string? shell = "powershell")
            {
                var e = HookEntry(cmd, shell);
                if (matcher != null) e["matcher"] = matcher;
                return e;
            }
            const string wakeStop = "\"C:\\Users\\x\\.brainx\\mcp\\brainx-mcp.exe\" hook-stop --vault \"G:\\Obsidian\" --agent claude-code --tag obsidianx-task-wake-v2";
            const string wakeStart = "\"C:\\Users\\x\\.brainx\\mcp\\brainx-mcp.exe\" hook-session-start --vault \"G:\\Obsidian\" --agent claude-code --tag obsidianx-task-wake-v2";

            // The owner's real v2 layout, one entry from before the installer
            // existed (no marker at all), and two hooks that are nobody's but the user's.
            var old = new JObject
            {
                ["model"] = "keep-me",
                ["hooks"] = new JObject
                {
                    ["SessionStart"] = new JArray(Entry(Ps("session-start.ps1", "# brainx-hooks v2"), null), Entry(wakeStart, null, null)),
                    ["UserPromptSubmit"] = new JArray(Entry(Ps("brain-prompt-gate.ps1", "# brainx-hooks v2"), null)),
                    ["PreToolUse"] = new JArray(Entry(Ps("brain-pretool-warn.ps1", ""), "Edit|Write|MultiEdit"), Entry("node C:/mine/guard.js", "Bash", null)),
                    ["PostToolUse"] = new JArray(
                        Entry(Ps("brain-tool-logger.ps1", "# brainx-hooks v2"), ".*"),
                        Entry(Ps("brain-error-recall.ps1", "# brainx-hooks v2"), "Bash|PowerShell"),
                        Entry(Ps("brain-auto-ingest.ps1", "# obsidianx-auto-ingest v5"), "Read|Edit|MultiEdit|Write")),
                    ["Stop"] = new JArray(Entry(Ps("stop-hook.ps1", "# brainx-hooks v2"), null), Entry(wakeStop, null, null)),
                    ["SessionEnd"] = new JArray(Entry(Ps("session-end.ps1", "# brainx-hooks v2"), null)),
                },
            };
            File.WriteAllText(settings, old.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(claude, "scripts", "brain-auto-ingest.ps1"),
                "try { Invoke-RestMethod -Uri 'http://localhost:6000/api/brain/auto-ingest' -Method Post } catch { }");

            var dry = await RunInstallHooks(exe, claude, vault, null, "--dry-run");
            Check("--dry-run succeeds and changes nothing", dry.Exit == 0 && JToken.DeepEquals(JObject.Parse(File.ReadAllText(settings)), old),
                  dry.Stdout + dry.Stderr);

            var r = await RunInstallHooks(exe, claude, vault, null);
            Check("install-hooks exits 0", r.Exit == 0, r.Stdout + r.Stderr);
            var s = JObject.Parse(File.ReadAllText(settings));
            var bytes = File.ReadAllBytes(settings);
            Check("settings.json has no BOM", bytes[0] == (byte)'{');
            Check("a backup of the old file is kept", Directory.GetFiles(claude, "settings.json.bak-*").Length == 1);

            var all = new[] { "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop" }.SelectMany(ev => Handlers(s, ev)).ToList();
            var native = all.Where(h => h["args"] is JArray).ToList();
            Check("exec form: five hooks and the auto-ingest name the binary directly, with no shell",
                  native.Count == 6 && native.All(h => h["shell"] == null && File.Exists((string?)h["command"])
                      && Path.GetFileNameWithoutExtension((string?)h["command"]) == "brainx-mcp"),
                  string.Join(" | ", native.Select(h => h.ToString(Formatting.None))));
            foreach (var (ev, matcher, sub, timeout) in new[]
                     {
                         ("UserPromptSubmit", (string?)null, "hook-prompt-gate", 15), ("PreToolUse", "Edit|Write|MultiEdit", "hook-pretool-warn", 10),
                         ("PostToolUse", ".*", "hook-tool-log", 10), ("PostToolUse", "Bash|PowerShell", "hook-error-recall", 10), ("Stop", null, "hook-brain-end", 15),
                     })
            {
                var group = (s["hooks"]![ev] as JArray)!.FirstOrDefault(g => (g["hooks"]![0]!["args"] as JArray)?[0]?.ToString() == sub);
                Check($"{sub} sits on {ev} with its matcher, timeout, vault and tag",
                      group != null && (string?)group["matcher"] == matcher && (int?)group["hooks"]![0]!["timeout"] == timeout
                      && group["hooks"]![0]!["args"]!.Select(x => x.ToString()).SequenceEqual(new[] { sub, "--vault", vault, "--tag", "brainx-hooks-v3" }),
                      group?.ToString(Formatting.None));
            }

            var text = File.ReadAllText(settings);
            Check("no ported script is left registered, marked or not",
                  new[] { "brain-tool-logger.ps1", "brain-prompt-gate.ps1", "brain-pretool-warn.ps1", "brain-error-recall.ps1", "stop-hook.ps1", "brain-auto-ingest.ps1" }
                      .All(x => !text.Contains(x, StringComparison.Ordinal)), text);
            Check("session-start and session-end stay scripts, in exec form: PowerShell boots once, not twice",
                  new[] { ("SessionStart", "session-start.ps1"), ("SessionEnd", "session-end.ps1"), ("PreCompact", "session-end.ps1") }.All(e =>
                      Handlers(s, e.Item1).Any(h => (string?)h["command"] == "powershell.exe" && h["shell"] == null
                          && (h["args"] as JArray)!.Select(x => x.ToString()).SequenceEqual(new[]
                             { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $"{scripts}/{e.Item2}", "--tag", "brainx-hooks-v3" }))),
                  string.Join(" | ", Handlers(s, "SessionStart").Select(h => h.ToString(Formatting.None))));
            Check("the wake hooks are the client's and were not touched",
                  Handlers(s, "Stop").Any(h => (string?)h["command"] == wakeStop) && Handlers(s, "SessionStart").Any(h => (string?)h["command"] == wakeStart));
            Check("a hook of the user's and an unrelated setting survive",
                  Handlers(s, "PreToolUse").Any(h => (string?)h["command"] == "node C:/mine/guard.js") && (string?)s["model"] == "keep-me");

            var ingest = (s["hooks"]!["PostToolUse"] as JArray)!.First(g => HookHealth.IsAutoIngestHook(HookHealth.CommandText(g["hooks"]![0])));
            Check("auto-ingest kept its matcher and the port the client baked, under a tag an older client accepts",
                  (string?)ingest["matcher"] == "Read|Edit|MultiEdit|Write" && text.Contains("http://localhost:6000/api/brain/auto-ingest")
                  && text.Contains("obsidianx-auto-ingest v5"), ingest.ToString(Formatting.None));

            Check("the ported scripts are not written again, what still runs on them is",
                  !File.Exists(Path.Combine(claude, "scripts", "brain-tool-logger.ps1"))
                  && File.Exists(Path.Combine(claude, "scripts", "session-start.ps1"))
                  && File.Exists(Path.Combine(claude, "scripts", "brain-aliases.json")));
            Check("hook health is quiet on the result", HookHealth.Problems(claude, TimeSpan.FromHours(48)).Count == 0,
                  string.Join(" | ", HookHealth.Problems(claude, TimeSpan.FromHours(48))));

            var again = await RunInstallHooks(exe, claude, vault, null);
            Check("a second run changes nothing and adds nothing", again.Exit == 0 && JToken.DeepEquals(JObject.Parse(File.ReadAllText(settings)), s),
                  again.Stdout + again.Stderr);

            // An opt-out is the absence of the entry, and must stay one.
            var optOut = JObject.Parse(File.ReadAllText(settings));
            (optOut["hooks"]!["PostToolUse"] as JArray)!.Where(g => HookHealth.IsAutoIngestHook(HookHealth.CommandText(g["hooks"]![0]))).ToList().ForEach(g => g.Remove());
            File.WriteAllText(settings, optOut.ToString(), new UTF8Encoding(false));
            await RunInstallHooks(exe, claude, vault, null);
            Check("an owner who removed auto-ingest does not get it back",
                  !File.ReadAllText(settings).Contains("hook-auto-ingest"));

            // A Claude Code older than exec form would ignore `args` and start a
            // bare MCP server once per tool call.
            var oldClaude = await RunInstallHooks(exe, claude, vault, "2.1.100 (Claude Code)");
            var shellForm = JObject.Parse(File.ReadAllText(settings));
            var hooks = new[] { "UserPromptSubmit", "PreToolUse", "PostToolUse", "Stop" }.SelectMany(ev => Handlers(shellForm, ev))
                .Where(h => HookHealth.IsInstallerHook(HookHealth.CommandText(h))).ToList();
            Check("(shell form) the scripts stay with their PowerShell shell and old marker",
                  Handlers(shellForm, "SessionStart").Any(h => (string?)h["shell"] == "powershell"
                      && ((string?)h["command"])!.EndsWith("session-start.ps1\" # brainx-hooks v3", StringComparison.Ordinal)));
            Check("an older Claude Code gets shell form: a quoted forward-slash path, no args, no shell field",
                  oldClaude.Exit == 0 && hooks.Count == 5 && hooks.All(h => h["args"] == null && h["shell"] == null
                      && ((string?)h["command"])!.StartsWith("\"", StringComparison.Ordinal) && !((string?)h["command"])!.Contains('\\')),
                  string.Join(" | ", hooks.Select(h => (string?)h["command"])));
            Check("(shell form) hook health recognises it and finds the binary", HookHealth.Problems(claude, TimeSpan.FromHours(48)).Count == 0,
                  string.Join(" | ", HookHealth.Problems(claude, TimeSpan.FromHours(48))));
            var back = await RunInstallHooks(exe, claude, vault, null);
            Check("and the next run, after an update, moves it to exec form with no duplicates",
                  back.Exit == 0 && Handlers(JObject.Parse(File.ReadAllText(settings)), "PostToolUse").Count(h => h["args"] is JArray) == 2);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static Task HookHealthKnowsNativeHooks()
    {
        var dir = TempDir("brainx-hookhealth-native-");
        try
        {
            var exe = Path.Combine(dir, "brainx-mcp.exe");
            File.WriteAllText(exe, "");
            JObject Exec(string command, string sub) => new()
            {
                ["hooks"] = new JArray(new JObject
                {
                    ["type"] = "command", ["command"] = command, ["timeout"] = 10,
                    ["args"] = new JArray(sub, "--vault", "G:\\Obsidian", "--tag", "brainx-hooks-v3"),
                }),
            };

            WriteSettings(dir, new JObject { ["PostToolUse"] = new JArray(Exec(exe, "hook-tool-log")) });
            File.WriteAllText(Path.Combine(dir, "tool-log.ndjson"), "{}\n");
            var healthy = HookHealth.Problems(dir, TimeSpan.FromHours(48));
            Check("exec-form hooks with a fresh log: nothing to say", healthy.Count == 0, string.Join(" | ", healthy));

            File.SetLastWriteTimeUtc(Path.Combine(dir, "tool-log.ndjson"), DateTime.UtcNow.AddDays(-3));
            var stale = HookHealth.Problems(dir, TimeSpan.FromHours(48));
            Check("hook-tool-log counts as the logger, so a stale log is named", stale.Any(x => x.Contains("tool-log.ndjson")), string.Join(" | ", stale));
            File.SetLastWriteTimeUtc(Path.Combine(dir, "tool-log.ndjson"), DateTime.UtcNow);

            WriteSettings(dir, new JObject { ["PostToolUse"] = new JArray(Exec(Path.Combine(dir, "gone", "brainx-mcp.exe"), "hook-tool-log")) });
            var gone = HookHealth.Problems(dir, TimeSpan.FromHours(48));
            Check("a hook whose binary is gone is named by its file", gone.Any(x => x.Contains("brainx-mcp.exe")), string.Join(" | ", gone));

            WriteSettings(dir, new JObject { ["Stop"] = new JArray(HookEntry($"\"{Path.Combine(dir, "gone", "brainx-mcp.exe").Replace('\\', '/')}\" hook-brain-end --tag brainx-hooks-v3", shell: null)) });
            var goneShell = HookHealth.Problems(dir, TimeSpan.FromHours(48));
            Check("so is one in shell form", goneShell.Any(x => x.Contains("brainx-mcp.exe")), string.Join(" | ", goneShell));

            // The rule from the 2026-09-21 update loop: where it points matters too.
            var managed = Path.Combine(McpRuntimePaths.AppRoot, "current", "mcp", "brainx-mcp.exe");
            WriteSettings(dir, new JObject { ["Stop"] = new JArray(Exec(managed, "hook-brain-end")) });
            var retired = HookHealth.Problems(dir, TimeSpan.FromHours(48));
            Check("a hook pinned inside the updater's folder is named", retired.Any(x => x.Contains("updater")), string.Join(" | ", retired));

            WriteSettings(dir, new JObject
            {
                ["PostToolUse"] = new JArray(new JObject { ["hooks"] = new JArray(new JObject
                {
                    ["type"] = "command", ["command"] = exe,
                    ["args"] = new JArray("hook-auto-ingest", "--tag", HookHealth.AutoIngestNativeTag),
                }) }),
            });
            Check("the native auto-ingest entry is recognised through its args",
                  HookHealth.IsAutoIngestHook(HookHealth.CommandText(JObject.Parse(File.ReadAllText(Path.Combine(dir, "settings.json")))["hooks"]!["PostToolUse"]![0]!["hooks"]![0])));

            var mcp = FindMcpExe();
            Check("a build with the hooks says so when asked", mcp != null && HookHealth.RunsNativeHooks(mcp));
            Check("no binary, no hooks", !HookHealth.RunsNativeHooks(Path.Combine(dir, "gone.exe")) && !HookHealth.RunsNativeHooks(null));
            // Standing in for a build from before this: exits 0, prints something else.
            var other = Path.Combine(Environment.SystemDirectory, "hostname.exe");
            if (File.Exists(other)) Check("a binary that answers anything else has none", !HookHealth.RunsNativeHooks(other));

            // Exec form names powershell.exe, which is no file of ours to find.
            var script = Path.Combine(dir, "session-start.ps1");
            JObject ScriptHook(string file) => new()
            {
                ["hooks"] = new JArray(new JObject
                {
                    ["type"] = "command", ["command"] = "powershell.exe",
                    ["args"] = new JArray("-NoProfile", "-File", file, "--tag", "brainx-hooks-v3"),
                }),
            };
            File.WriteAllText(script, "exit 0");
            WriteSettings(dir, new JObject { ["SessionStart"] = new JArray(ScriptHook(script)) });
            var fine = HookHealth.Problems(dir, TimeSpan.FromHours(48));
            Check("an exec-form script that exists is healthy, and powershell.exe is not called missing", fine.Count == 0, string.Join(" | ", fine));
            File.Delete(script);
            var noScript = HookHealth.Problems(dir, TimeSpan.FromHours(48));
            Check("an exec-form script that is gone is named by its file", noScript.Any(x => x.Contains("session-start.ps1")), string.Join(" | ", noScript));

            Environment.SetEnvironmentVariable("BRAINX_CLAUDE_VERSION", "2.1.138 (Claude Code)");
            Check("2.1.138 has no exec form", !HookHealth.ExecFormSupported());
            Environment.SetEnvironmentVariable("BRAINX_CLAUDE_VERSION", "2.1.139 (Claude Code)");
            Check("2.1.139 has it", HookHealth.ExecFormSupported());
            Environment.SetEnvironmentVariable("BRAINX_CLAUDE_VERSION", "");
            Check("not finding a version is no evidence of an old one: the Desktop app carries its own", HookHealth.ExecFormSupported());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRAINX_CLAUDE_VERSION", null);
            try { Directory.Delete(dir, true); } catch { }
        }
        return Task.CompletedTask;
    }
}
