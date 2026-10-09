using System.Diagnostics;
using System.Text;
using BrainX.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

// The brain hooks died silently for two weeks (2026-09-25 to 10-09): a rewrite
// of ~/.claude/settings.json dropped "shell": "powershell", Git Bash turned
// $env:USERPROFILE into ":USERPROFILE", and nothing anywhere said so. These
// checks hold the two things built in response: the MCP-side health check
// that can see it, and the SessionEnd/PreCompact digest that keeps a closed
// session from vanishing.
internal static partial class Program
{
    private static void RegisterHookHealthChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("hook health: the 2026-09-25 signature, a missing script and a stale log are each named", HookHealthNamesEachFailure));
        checks.Add(("hook health: a healthy, an absent and a foreign-only config report nothing", HookHealthQuietWhenFine));
        checks.Add(("session-end hook: a digest is written once per session, redacted, and skipped after a handoff", SessionEndDigest));
    }

    private static string TempDir(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static JObject HookEntry(string command, string? shell = "powershell") =>
        new() { ["hooks"] = new JArray(shell == null
            ? new JObject { ["type"] = "command", ["command"] = command }
            : new JObject { ["type"] = "command", ["command"] = command, ["shell"] = shell }) };

    private static void WriteSettings(string dir, JObject hooks) =>
        File.WriteAllText(Path.Combine(dir, "settings.json"), new JObject { ["hooks"] = hooks }.ToString());

    private static Task HookHealthNamesEachFailure()
    {
        var dir = TempDir("brainx-hookhealth-");
        try
        {
            var scripts = Path.Combine(dir, "scripts");
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "brain-tool-logger.ps1"), "exit 0");
            var fwd = scripts.Replace('\\', '/');

            // Exactly what was on disk after the rewrite.
            WriteSettings(dir, new JObject
            {
                ["Stop"] = new JArray(HookEntry("powershell -NoProfile -File \"$env:USERPROFILE\\.claude\\scripts\\stop-hook.ps1\" # brainx-hooks v1", shell: null)),
                ["PostToolUse"] = new JArray(
                    HookEntry($"powershell -NoProfile -File \"{fwd}/brain-tool-logger.ps1\" # brainx-hooks v2"),
                    HookEntry($"powershell -NoProfile -File \"{fwd}/brain-error-recall.ps1\" # brainx-hooks v2")),
            });
            var log = Path.Combine(dir, "tool-log.ndjson");
            File.WriteAllText(log, "{}\n");
            File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddDays(-3));

            var p = HookHealth.Problems(dir, TimeSpan.FromHours(48));
            var all = string.Join(" | ", p);
            Check("$env: without shell is named", p.Any(x => x.Contains("without \"shell\"")), all);
            Check("the missing script is named by file", p.Any(x => x.Contains("brain-error-recall.ps1")), all);
            Check("the existing script is not named", !all.Contains("brain-tool-logger.ps1"), all);
            Check("a 3-day-old tool log is named", p.Any(x => x.Contains("tool-log.ndjson")), all);
            Check("the instructions line leads with HOOKS BROKEN",
                  HookHealth.InstructionsLine(dir, TimeSpan.FromHours(48)).StartsWith("HOOKS BROKEN:", StringComparison.Ordinal));

            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ not json");
            Check("an unparseable settings.json is named", HookHealth.Problems(dir, TimeSpan.FromHours(48)).Any(x => x.Contains("does not parse")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task HookHealthQuietWhenFine()
    {
        var dir = TempDir("brainx-hookhealth-");
        try
        {
            Check("no settings.json: nothing to say", HookHealth.Problems(dir, TimeSpan.FromHours(48)).Count == 0);

            WriteSettings(dir, new JObject
            {
                ["Stop"] = new JArray(HookEntry("node C:/somewhere/else.js", shell: null)),
            });
            Check("only someone else's hooks: nothing to say", HookHealth.Problems(dir, TimeSpan.FromHours(48)).Count == 0);

            var scripts = Path.Combine(dir, "scripts");
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "brain-tool-logger.ps1"), "exit 0");
            WriteSettings(dir, new JObject
            {
                // $env: is fine WITH the shell field: PowerShell resolves it.
                ["Stop"] = new JArray(HookEntry("powershell -NoProfile -File \"$env:USERPROFILE\\.claude\\scripts\\stop-hook.ps1\" # brainx-hooks v1")),
                ["PostToolUse"] = new JArray(HookEntry($"powershell -NoProfile -File \"{scripts.Replace('\\', '/')}/brain-tool-logger.ps1\" # brainx-hooks v2")),
            });
            File.WriteAllText(Path.Combine(dir, "tool-log.ndjson"), "{}\n");
            var p = HookHealth.Problems(dir, TimeSpan.FromHours(48));
            Check("a healthy config with a fresh log: nothing to say", p.Count == 0, string.Join(" | ", p));
            Check("healthy: the instructions line is empty", HookHealth.InstructionsLine(dir, TimeSpan.FromHours(48)) == "");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        return Task.CompletedTask;
    }

    private static string? FindHookScript(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "hooks", name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string Line(JObject o) => o.ToString(Formatting.None);

    private static async Task<int> RunSessionEnd(string script, string home, string vault, string transcript, string sid, string hookEvent)
    {
        var psi = new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        psi.Environment["USERPROFILE"] = home;        // decision log lands in the temp home, not the owner's
        psi.Environment["BRAINX_VAULT"] = vault;
        psi.Environment["BRAINX_SANDBOX"] = "1";      // and nothing is posted to a running client
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(new JObject
        {
            ["session_id"] = sid, ["transcript_path"] = transcript, ["cwd"] = @"D:\Code\demo-app", ["hook_event_name"] = hookEvent,
        }.ToString(Formatting.None));
        p.StandardInput.Close();
        await p.StandardOutput.ReadToEndAsync();
        await p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000)) { try { p.Kill(); } catch { } return -1; }
        return p.ExitCode;
    }

    private static async Task SessionEndDigest()
    {
        var script = FindHookScript("session-end.ps1");
        if (script == null) { Check("hooks/session-end.ps1 found", false); return; }
        var root = TempDir("brainx-sessionend-");
        try
        {
            var home = Path.Combine(root, "home");
            var vault = Path.Combine(root, "vault");
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
            var autoDir = Path.Combine(vault, "Notes", "Claude-Sessions", "auto");
            const string secret = "sk-abcdef1234567890abcdef1234";

            string User(string text) => Line(new JObject { ["type"] = "user", ["timestamp"] = "2026-10-10T01:00:00Z",
                ["message"] = new JObject { ["role"] = "user", ["content"] = text } });
            string Assistant(JArray content) => Line(new JObject { ["type"] = "assistant", ["timestamp"] = "2026-10-10T01:05:00Z",
                ["message"] = new JObject { ["role"] = "assistant", ["content"] = content } });

            var t1 = Path.Combine(root, "t1.jsonl");
            File.WriteAllLines(t1, new[]
            {
                User("fix the login bug, the key is api_key=" + secret),
                User("<system-reminder>harness text, not the owner</system-reminder>"),
                Assistant(new JArray(new JObject { ["type"] = "tool_use", ["id"] = "a", ["name"] = "Edit",
                    ["input"] = new JObject { ["file_path"] = @"D:\Code\demo-app\Login.cs", ["old_string"] = "x", ["new_string"] = "y" } })),
                Line(new JObject { ["type"] = "user", ["message"] = new JObject { ["role"] = "user",
                    ["content"] = new JArray(new JObject { ["type"] = "tool_result", ["tool_use_id"] = "a", ["content"] = "ok" }) } }),
                Line(new JObject { ["type"] = "user", ["isCompactSummary"] = true, ["message"] = new JObject { ["role"] = "user",
                    ["content"] = "This session is being continued from a previous conversation that ran out of context. Summary: ..." } }),
                Assistant(new JArray(new JObject { ["type"] = "tool_use", ["id"] = "s", ["name"] = "Write",
                    ["input"] = new JObject { ["file_path"] = @"D:\Temp\User\claude\x\scratchpad\probe.mjs", ["content"] = "1" } })),
                User("ใช้ได้แล้ว ขอบคุณ"),
                Assistant(new JArray(new JObject { ["type"] = "text", ["text"] = "Fixed: Login.cs now checks the session token." })),
            }, new UTF8Encoding(false));

            var rc = await RunSessionEnd(script, home, vault, t1, "11111111-aaaa", "PreCompact");
            Check("PreCompact run exits 0", rc == 0, $"exit {rc}");
            var notes = Directory.Exists(autoDir) ? Directory.GetFiles(autoDir, "*.md") : Array.Empty<string>();
            Check("one digest note written", notes.Length == 1, string.Join(", ", notes));
            if (notes.Length == 1)
            {
                var body = File.ReadAllText(notes[0], Encoding.UTF8);
                Check("the note is keyed by the session id", Path.GetFileName(notes[0]).EndsWith(" 11111111.md", StringComparison.Ordinal), notes[0]);
                Check("the changed file is listed", body.Contains(@"D:\Code\demo-app\Login.cs"));
                Check("the owner prompt is kept, Thai intact", body.Contains("fix the login bug") && body.Contains("ใช้ได้แล้ว"));
                Check("the secret is redacted", !body.Contains(secret) && body.Contains("***"));
                Check("harness wrappers are not counted as the owner", !body.Contains("harness text"));
                Check("a compaction summary is not counted as the owner", !body.Contains("being continued from a previous conversation"));
                Check("scratchpad writes are counted, not listed", !body.Contains("probe.mjs") && body.Contains("(+1 scratchpad writes)"));
                Check("the last reply is kept", body.Contains("checks the session token"));
                Check("tagged session-auto-digest", body.Contains("session-auto-digest"));
            }

            rc = await RunSessionEnd(script, home, vault, t1, "11111111-aaaa", "SessionEnd");
            Check("SessionEnd after PreCompact still leaves exactly one note",
                  rc == 0 && Directory.GetFiles(autoDir, "*.md").Length == 1);

            // A session that already handed off on its own gets no digest.
            var t2 = Path.Combine(root, "t2.jsonl");
            File.WriteAllLines(t2, new[]
            {
                User("first"), User("second"),
                Assistant(new JArray(new JObject { ["type"] = "tool_use", ["id"] = "b", ["name"] = "mcp__brainx-brain__brain_create_note",
                    ["input"] = new JObject { ["title"] = "Session 2026-10-10 - demo", ["tags"] = "session-handoff, demo", ["content"] = "x" } })),
            }, new UTF8Encoding(false));
            await RunSessionEnd(script, home, vault, t2, "22222222-bbbb", "SessionEnd");
            Check("a session with its own #session-handoff gets no digest",
                  !Directory.GetFiles(autoDir, "* 22222222.md").Any());

            // One prompt, no edits: not worth a note.
            var t3 = Path.Combine(root, "t3.jsonl");
            File.WriteAllLines(t3, new[] { User("what time is it") }, new UTF8Encoding(false));
            await RunSessionEnd(script, home, vault, t3, "33333333-cccc", "SessionEnd");
            Check("a trivial session gets no digest", !Directory.GetFiles(autoDir, "* 33333333.md").Any());

            var decisions = Path.Combine(home, ".claude", "brain-session-end.ndjson");
            Check("every run leaves a decision line in the (temp) home",
                  File.Exists(decisions) && File.ReadAllLines(decisions).Length == 4);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
