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
        checks.Add(("session-end hook: a session that used Claude in Chrome leaves no reply and no URL in the vault", SessionEndPrivateBrowsing));
    }

    /// <summary>
    /// The owner's rule: what Claude in Chrome showed never enters the vault.
    /// A closing reply restates the page ("your balance is …") and a prompt
    /// names the URL, so a digest of such a session drops the replies and the
    /// URLs. Negative control: the SAME transcript without the chrome call
    /// keeps both — the stripping is caused by the detection, nothing else.
    /// </summary>
    private static async Task SessionEndPrivateBrowsing()
    {
        var script = FindHookScript("session-end.ps1");
        if (script == null) { Check("hooks/session-end.ps1 found", false); return; }
        var root = TempDir("brainx-sessionend-private-");
        try
        {
            var home = Path.Combine(root, "home");
            var vault = Path.Combine(root, "vault");
            Directory.CreateDirectory(Path.Combine(home, ".claude"));
            Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
            var autoDir = Path.Combine(vault, "Notes", "Claude-Sessions", "auto");

            const string url = "https://bank.example/account?acct=998877";
            const string pageFact = "ยอดเงินคงเหลือ 54,321 บาท";
            string User(string text) => Line(new JObject { ["type"] = "user", ["timestamp"] = "2026-10-10T02:00:00Z",
                ["message"] = new JObject { ["role"] = "user", ["content"] = text } });
            string Assistant(JArray content, bool sidechain = false)
            {
                var o = new JObject { ["type"] = "assistant", ["timestamp"] = "2026-10-10T02:05:00Z",
                    ["message"] = new JObject { ["role"] = "assistant", ["content"] = content } };
                if (sidechain) o["isSidechain"] = true;
                return Line(o);
            }
            string[] Transcript(string? chromeTool, bool sidechain = false)
            {
                var lines = new List<string>
                {
                    User("open " + url + " and tell me the balance"),
                    User("ขอบคุณ แล้วสรุปให้หน่อย"),
                };
                if (chromeTool != null)
                    lines.Add(Assistant(new JArray(new JObject { ["type"] = "tool_use", ["id"] = "c1", ["name"] = chromeTool,
                        ["input"] = new JObject { ["url"] = url } }), sidechain));
                lines.Add(Assistant(new JArray(new JObject { ["type"] = "text", ["text"] = "The page says " + pageFact + "." })));
                return lines.ToArray();
            }

            async Task<string?> Digest(string sid, string[] lines)
            {
                var t = Path.Combine(root, sid + ".jsonl");
                File.WriteAllLines(t, lines, new UTF8Encoding(false));
                var rc = await RunSessionEnd(script, home, vault, t, sid + "-0000", "SessionEnd");
                if (rc != 0) return null;
                var f = Directory.Exists(autoDir) ? Directory.GetFiles(autoDir, $"* {sid}.md").FirstOrDefault() : null;
                return f == null ? null : File.ReadAllText(f, Encoding.UTF8);
            }

            // Control first: no chrome call — the reply and the URL ARE kept.
            var plain = await Digest("aaaa0001", Transcript(null));
            Check("(control) without a chrome call the digest keeps the reply and the URL",
                  plain != null && plain.Contains(pageFact) && plain.Contains(url), plain?[..Math.Min(400, plain.Length)]);

            foreach (var (sid, tool, side) in new[]
                     {
                         ("bbbb0002", "mcp__brainx-brain__chrome__get_page_text", false),
                         ("cccc0003", "mcp__brainx-brain__chrome_navigate", false),          // the flat spelling
                         ("dddd0004", "mcp__brainx-brain__private_browse_search", false),
                         ("eeee0005", "mcp__brainx-brain__chrome__navigate", true),          // a subagent did it
                         ("ffff0006", "mcp__claude-in-chrome__navigate", false),            // Claude Code's own server
                     })
            {
                var body = await Digest(sid, Transcript(tool, side));
                Check($"{tool}{(side ? " (sidechain)" : "")}: a digest is still written, saying why it is reduced",
                      body != null && body.Contains("Private browsing (Claude in Chrome) was used"), body?[..Math.Min(300, body?.Length ?? 0)]);
                Check($"…without the reply that restated the page", body != null && !body.Contains(pageFact) && !body.Contains("## Last replies"));
                Check($"…and without the URL, the owner's words kept", body != null && !body.Contains("bank.example") && body.Contains("[url removed]")
                      && body.Contains("ขอบคุณ แล้วสรุปให้หน่อย"));
            }

            var decisions = File.ReadAllLines(Path.Combine(home, ".claude", "brain-session-end.ndjson"));
            Check("the decision log marks the private sessions (and only them)",
                  decisions.Count(d => d.Contains("\"private_browsing\":true")) == 5
                  && decisions.Count(d => d.Contains("\"private_browsing\":false")) == 1, string.Join(" | ", decisions));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
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

    private static string? FindHookScript(string name) => FindRepoFile("hooks", name);

    /// <summary>
    /// A file of the checkout: walked up from the harness binary (an in-tree
    /// build) and from the working folder (a build sent to a shared output
    /// folder, run from the checkout — how locked.ps1 runs it).
    /// </summary>
    private static string? FindRepoFile(params string[] parts)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var d = new DirectoryInfo(start); d != null; d = d.Parent)
            {
                var p = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray());
                if (File.Exists(p)) return p;
            }
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
