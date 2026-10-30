using System.Diagnostics;
using System.Text;
using BrainX.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

// An update reaches no settings.json by itself, so BrainX.Client upgrades what
// `install-hooks` wrote and moves its own wake hooks to exec form. Both run
// unattended on the owner's global Claude config at every start, which makes what
// they must NOT do the larger half of these checks: add a hook to a machine that
// has none, bring back one that was taken out, stack a duplicate, write on a start
// with nothing to do. Every run below names a sandbox as its config dir.
internal static partial class Program
{
    private static void RegisterHookUpgradeChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("wake hooks: exec form replaces every older entry and nothing else", WakeHooksReplaceTheirOwn));
        checks.Add(("wake hooks: the binary answers an exec-form entry as it did a shell one", WakeHooksRunInExecForm));
        checks.Add(("hook upgrade: an install is outdated only with installer entries that are behind", InstallerHooksJudged));
        checks.Add(("hook upgrade: an old install upgrades once, in place, with no duplicates", OldInstallUpgrades));
        checks.Add(("hook upgrade: a machine with no installer hooks is left exactly as it is", NoInstallerHooksNoUpgrade));
        checks.Add(("hook upgrade: a hook the owner took out stays out, and theirs stay in", UpgradeRespectsTheOwner));
    }

    private const string UserGuard = "node C:/mine/guard.js";

    private static async Task<HookRun> RunHandler(JToken handler, string stdin, string home, string vault)
    {
        var psi = new ProcessStartInfo((string)handler["command"]!);
        foreach (var a in handler["args"]!) psi.ArgumentList.Add(a.ToString());
        return await RunProcess(psi, stdin, home, vault);
    }

    private static Task WakeHooksReplaceTheirOwn()
    {
        const string exe = @"C:\Users\x y\.brainx\mcp\brainx-mcp.exe";
        const string vault = @"G:\My Vault\";
        var exec = ClaudeWakeHooks.Handler(exe, "hook-stop", vault, execForm: true);
        var shell = ClaudeWakeHooks.Handler(exe, "hook-stop", vault, execForm: false);

        Check("exec form: the exe alone in command, its arguments in args, no shell",
              (string?)exec["command"] == exe && exec["shell"] == null && (int?)exec["timeout"] == 10
              && exec["args"]!.Select(a => a.ToString()).SequenceEqual(new[]
                 { "hook-stop", "--vault", @"G:\My Vault", "--agent", "claude-code", "--tag", ClaudeWakeHooks.ExecTag }),
              exec.ToString(Formatting.None));
        Check("shell form is the entry the client has always written",
              (string?)shell["command"] == $"\"{exe}\" hook-stop --vault \"{vault}\" --agent claude-code --tag obsidianx-task-wake-v2"
              && shell["args"] == null, shell.ToString(Formatting.None));
        // That client reads `command` alone. Without this it would add a shell-form
        // entry beside every exec-form one on a machine that still runs it.
        Check("a client older than this finds its own tag in the exec-form entry",
              exec.ToString().Contains("obsidianx-task-wake-v2", StringComparison.Ordinal));

        var everyOldShape = new[]
        {
            "\"C:\\Users\\x\\.brainx\\mcp\\brainx-mcp.exe\" hook-stop --vault \"G:\\Obsidian\"",
            "\"C:\\old\\mcp\\brainx-mcp.exe\" hook-stop --vault \"G:\\Obsidian\" --agent claude-code --tag obsidianx-task-wake-v2",
        };
        var arr = new JArray(everyOldShape.Select(c => (object)HookEntry(c, null)).ToArray());
        arr.Add(new JObject { ["hooks"] = new JArray(ClaudeWakeHooks.Handler(@"C:\old\brainx-mcp.exe", "hook-stop", vault, execForm: true)) });
        arr.Add(HookEntry(UserGuard, null));
        // A later port may well be called something like this.
        var installerEntry = new JObject
        {
            ["type"] = "command", ["command"] = exe,
            ["args"] = new JArray("hook-stop-digest", "--vault", vault, "--tag", HookHealth.InstallerTag),
        };
        arr.Add(new JObject { ["hooks"] = new JArray(installerEntry) });
        arr.Add(new JObject
        {
            ["hooks"] = new JArray(
                new JObject { ["type"] = "command", ["command"] = "\"C:\\old\\brainx-mcp.exe\" hook-stop --vault \"G:\\Obsidian\"" },
                new JObject { ["type"] = "command", ["command"] = "node C:/mine/shared-group.js" }),
        });

        var once = ClaudeWakeHooks.Upsert(arr, ClaudeWakeHooks.Handler(exe, "hook-stop", vault, execForm: true));
        var handlers = once.SelectMany(g => g["hooks"] as JArray ?? new JArray()).ToList();
        Check("every older wake entry is gone and one exec-form entry stands, whatever shape they had",
              handlers.Count(ClaudeWakeHooks.IsWakeHook) == 1 && handlers.Single(ClaudeWakeHooks.IsWakeHook)["args"] is JArray,
              once.ToString(Formatting.None));
        Check("the owner's hooks survive, one that shared a group with ours included",
              handlers.Any(h => (string?)h["command"] == UserGuard) && handlers.Any(h => (string?)h["command"] == "node C:/mine/shared-group.js"));
        Check("an entry of the installer is never taken for a wake hook, whatever it is called",
              handlers.Any(h => HookHealth.IsInstallerHook(HookHealth.CommandText(h))));

        var before = once.ToString(Formatting.None);
        var twice = ClaudeWakeHooks.Upsert(once, ClaudeWakeHooks.Handler(exe, "hook-stop", vault, execForm: true));
        Check("a second run leaves the same array", twice.ToString(Formatting.None) == before, twice.ToString(Formatting.None));
        Check("a new array needs no existing one", ClaudeWakeHooks.Upsert(null, exec).Count == 1);

        string Settings(params JToken[] stop) => new JObject
        {
            ["hooks"] = new JObject
            {
                ["Stop"] = new JArray(stop),
                ["SessionStart"] = new JArray(new JObject { ["hooks"] = new JArray(ClaudeWakeHooks.Handler(exe, "hook-session-start", vault, true)) }),
            },
        }.ToString();
        var one = new JObject { ["hooks"] = new JArray(exec) };
        var oneShell = new JObject { ["hooks"] = new JArray(ClaudeWakeHooks.Handler(exe, "hook-stop", vault, false)) };
        Check("current: one entry per event, under the tag, naming the exe",
              ClaudeWakeHooks.IsCurrent(Settings(one), exe, ClaudeWakeHooks.ExecTag));
        Check("not current: another exe, though another entry names this one",
              !ClaudeWakeHooks.IsCurrent(Settings(one), @"C:\new\brainx-mcp.exe", ClaudeWakeHooks.ExecTag));
        Check("not current: two entries on one event", !ClaudeWakeHooks.IsCurrent(Settings(one, one), exe, ClaudeWakeHooks.ExecTag));
        Check("not current: one event has none", !ClaudeWakeHooks.IsCurrent(Settings(), exe, ClaudeWakeHooks.ExecTag));
        Check("not current: exec tag asked of a shell entry, and the other way about",
              !ClaudeWakeHooks.IsCurrent(Settings(oneShell), exe, ClaudeWakeHooks.ExecTag)
              && !ClaudeWakeHooks.IsCurrent(Settings(one), exe, ClaudeWakeHooks.ShellTag));
        Check("not current, and no throw: no file, no hooks, a corrupt file",
              !ClaudeWakeHooks.IsCurrent("", exe, ClaudeWakeHooks.ExecTag) && !ClaudeWakeHooks.IsCurrent("{}", exe, ClaudeWakeHooks.ExecTag)
              && !ClaudeWakeHooks.IsCurrent("{ nope", exe, ClaudeWakeHooks.ExecTag));
        return Task.CompletedTask;
    }

    private static async Task WakeHooksRunInExecForm()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var root = TempDir("brainx-wake-exec-");
        try
        {
            var home = Path.Combine(root, "home");
            var vault = Path.Combine(root, "My Vault") + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(home);
            Directory.CreateDirectory(Path.Combine(vault, "Tasks"));

            var stop = ClaudeWakeHooks.Handler(exe, "hook-stop", vault, execForm: true);
            var start = ClaudeWakeHooks.Handler(exe, "hook-session-start", vault, execForm: true);

            var quiet = await RunHandler(stop, "{}", home, vault);
            Check("nothing queued: exit 0, so the turn ends", quiet.Exit == 0, quiet.Stdout + quiet.Stderr);

            File.WriteAllText(Path.Combine(vault, "Tasks", "t.md"),
                "---\ntask_id: t-upgrade-1\nstatus: open\nassignee: claude-code\npriority: normal\n---\n# Finish the upgrade\n", new UTF8Encoding(false));

            var opening = await RunHandler(start, "{}", home, vault);
            Check("SessionStart: exit 0 and the task on stdout, so it becomes context",
                  opening.Exit == 0 && opening.Stdout.Contains("Finish the upgrade", StringComparison.Ordinal), opening.Stdout + opening.Stderr);

            var woken = await RunHandler(stop, "{}", home, vault);
            Check("Stop: exit 2 and the task on stderr, so the model carries on",
                  woken.Exit == 2 && woken.Stderr.Contains("Finish the upgrade", StringComparison.Ordinal), woken.Stdout + woken.Stderr);

            var again = await RunHandler(stop, "{\"stop_hook_active\":true}", home, vault);
            Check("Stop after a wake: exit 0, so a wake cannot become a loop", again.Exit == 0, again.Stdout + again.Stderr);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static Task InstallerHooksJudged()
    {
        var dir = TempDir("brainx-hook-outdated-");
        var old = Environment.GetEnvironmentVariable("BRAINX_CLAUDE_VERSION");
        try
        {
            var exe = Path.Combine(dir, "brainx-mcp.exe");
            File.WriteAllText(exe, "");
            var retired = Path.Combine(dir, "AppData", "Local", "xjanova", "brainx-mcp", "brainx-mcp.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(retired)!);
            File.WriteAllText(retired, "");
            var script = Path.Combine(dir, "session-start.ps1").Replace('\\', '/');
            File.WriteAllText(script, "");

            JObject Native(string at) => new()
            {
                ["hooks"] = new JArray(new JObject
                {
                    ["type"] = "command", ["command"] = at,
                    ["args"] = new JArray("hook-tool-log", "--vault", "V", "--tag", HookHealth.InstallerTag),
                }),
            };
            JObject Stop(params JObject[] entries) => new() { ["Stop"] = new JArray(entries.Cast<object>().ToArray()) };

            Check("no settings.json: nothing to upgrade", !HookHealth.InstallerHooksOutdated(dir));

            File.WriteAllText(Path.Combine(dir, "settings.json"), "{ nope");
            Check("a settings.json that does not parse is not touched", !HookHealth.InstallerHooksOutdated(dir));

            WriteSettings(dir, Stop(HookEntry(UserGuard, null),
                HookEntry("\"C:\\x\\brainx-mcp.exe\" hook-stop --vault \"G:\\O\" --tag obsidianx-task-wake-v2", null)));
            Check("only the owner's hooks and a wake hook: none of the installer's, nothing to upgrade",
                  !HookHealth.InstallerHooksOutdated(dir));

            WriteSettings(dir, Stop(HookEntry($"powershell -NoProfile -File \"{script}\" # brainx-hooks v2")));
            Check("a v2 entry is behind", HookHealth.InstallerHooksOutdated(dir));

            WriteSettings(dir, Stop(Native(exe)));
            Check("a current native entry is not", !HookHealth.InstallerHooksOutdated(dir));

            WriteSettings(dir, Stop(Native(Path.Combine(dir, "gone", "brainx-mcp.exe"))));
            Check("a native entry whose binary is gone is", HookHealth.InstallerHooksOutdated(dir));
            WriteSettings(dir, Stop(Native(retired)));
            Check("so is one running where the updater replaces it", HookHealth.InstallerHooksOutdated(dir));

            var shellForm = HookEntry($"powershell -NoProfile -File \"{script}\" {HookHealth.InstallerScriptMark}");
            WriteSettings(dir, Stop(shellForm));
            Environment.SetEnvironmentVariable("BRAINX_CLAUDE_VERSION", "2.1.138 (Claude Code)");
            Check("shell form at this version stays while Claude Code is too old for exec form", !HookHealth.InstallerHooksOutdated(dir));
            Environment.SetEnvironmentVariable("BRAINX_CLAUDE_VERSION", "2.1.139 (Claude Code)");
            Check("and is behind once it is not", HookHealth.InstallerHooksOutdated(dir));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRAINX_CLAUDE_VERSION", old);
            try { Directory.Delete(dir, true); } catch { }
        }
        return Task.CompletedTask;
    }

    /// <summary>The owner's real v2 layout, wake hooks and auto-ingest included,
    /// plus a hook of their own. Same shape as a live settings.json on 2026-10-30.</summary>
    private static JObject OldInstall(string claude, string wakeExe)
    {
        var scripts = Path.Combine(claude, "scripts").Replace('\\', '/');
        Directory.CreateDirectory(scripts);
        string Ps(string s, string mark) => $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scripts}/{s}\" {mark}";
        JObject Entry(string cmd, string? matcher = null, string? shell = "powershell")
        {
            var e = HookEntry(cmd, shell);
            if (matcher != null) e["matcher"] = matcher;
            return e;
        }
        const string v2 = "# brainx-hooks v2";
        var wake = $"--vault \"G:\\Obsidian\" --agent claude-code --tag {ClaudeWakeHooks.ShellTag}";
        return new JObject
        {
            ["model"] = "keep-me",
            ["hooks"] = new JObject
            {
                ["SessionStart"] = new JArray(Entry(Ps("session-start.ps1", v2)), Entry($"\"{wakeExe}\" hook-session-start {wake}", null, null)),
                ["UserPromptSubmit"] = new JArray(Entry(Ps("brain-prompt-gate.ps1", v2))),
                ["PreToolUse"] = new JArray(Entry(Ps("brain-pretool-warn.ps1", v2), "Edit|Write|MultiEdit"), Entry(UserGuard, "Bash", null)),
                ["PostToolUse"] = new JArray(
                    Entry(Ps("brain-tool-logger.ps1", v2), ".*"),
                    Entry(Ps("brain-error-recall.ps1", v2), "Bash|PowerShell"),
                    Entry(Ps("brain-auto-ingest.ps1", "# obsidianx-auto-ingest v5"), "Read|Edit|MultiEdit|Write")),
                ["Stop"] = new JArray(Entry(Ps("stop-hook.ps1", v2)), Entry($"\"{wakeExe}\" hook-stop {wake}", null, null)),
                ["SessionEnd"] = new JArray(Entry(Ps("session-end.ps1", v2))),
                ["PreCompact"] = new JArray(Entry(Ps("session-end.ps1", v2))),
            },
        };
    }

    private static int CountOf(JObject settings, string what) =>
        ((JObject)settings["hooks"]!).Properties().SelectMany(p => (JArray)p.Value)
            .SelectMany(g => g["hooks"] as JArray ?? new JArray())
            .Count(h => HookHealth.CommandText(h).Contains(what, StringComparison.Ordinal));

    private static async Task OldInstallUpgrades()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var root = TempDir("brainx-hook-upgrade-");
        var old = Environment.GetEnvironmentVariable("BRAINX_CLAUDE_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("BRAINX_CLAUDE_VERSION", "2.1.296 (Claude Code)");
            var claude = Path.Combine(root, "home", ".claude");
            var vault = Path.Combine(root, "My Vault");
            Directory.CreateDirectory(vault);
            var settings = Path.Combine(claude, "settings.json");
            var install = OldInstall(claude, exe);
            File.WriteAllText(settings, install.ToString(), new UTF8Encoding(false));

            // A binary from before the native hooks: a build that lost hook-probe
            // would write the scripts back, and a half-failed mirror still has an exe.
            var before = File.ReadAllBytes(settings);
            var notABrain = Path.Combine(Environment.SystemDirectory, "whoami.exe");
            Check("a binary with no native hooks is not asked to install them",
                  !HookHealth.UpgradeInstallerHooks(claude, notABrain, vault) && File.ReadAllBytes(settings).SequenceEqual(before));
            Check("nor is a binary that is not there",
                  !HookHealth.UpgradeInstallerHooks(claude, null, vault) && File.ReadAllBytes(settings).SequenceEqual(before));

            Check("the upgrade runs and reports it", HookHealth.UpgradeInstallerHooks(claude, exe, vault));
            var s = JObject.Parse(File.ReadAllText(settings));
            var text = s.ToString();

            // What fails if a script is ported without saying which hook replaced it
            // (RetiredScripts): `--upgrade` adds only what it can match to an old entry.
            Check("as many handlers after as before: an upgrade neither adds nor drops one",
                  CountOf(s, "") == CountOf(install, ""), text);
            Check("no script the binary replaced is still registered",
                  new[] { "brain-prompt-gate.ps1", "brain-pretool-warn.ps1", "brain-tool-logger.ps1", "brain-error-recall.ps1", "stop-hook.ps1" }
                      .All(x => !text.Contains(x, StringComparison.Ordinal)), text);
            foreach (var sub in new[] { "hook-prompt-gate", "hook-pretool-warn", "hook-tool-log", "hook-error-recall", "hook-brain-end", "hook-auto-ingest" })
                Check($"{sub} is registered exactly once", CountOf(s, " " + sub) == 1, text);
            Check("session-start is registered once and session-end on its two events",
                  CountOf(s, "session-start.ps1") == 1 && CountOf(s, "session-end.ps1") == 2, text);
            Check("the wake hooks are the client's and are still there, once each",
                  CountOf(s, " hook-stop ") == 1 && CountOf(s, " hook-session-start ") == 1, text);
            Check("the owner's hook and an unrelated setting survive", CountOf(s, UserGuard) == 1 && (string?)s["model"] == "keep-me", text);
            Check("the binary was not left pinned to a script's shell",
                  ((JArray)s["hooks"]!["Stop"]!).SelectMany(g => g["hooks"]!).All(h => h["shell"] == null));
            Check("no longer outdated, so the tag the binary writes is the one the client looks for",
                  !HookHealth.InstallerHooksOutdated(claude));
            Check("the old file is kept as a backup", Directory.GetFiles(claude, "settings.json.bak-*").Length == 1);
            Check("no temp file is left beside it", Directory.GetFiles(claude, "*.tmp").Length == 0);

            var after = File.ReadAllBytes(settings);
            var stamp = File.GetLastWriteTimeUtc(settings);
            Check("the next start finds nothing to do", !HookHealth.UpgradeInstallerHooks(claude, exe, vault));
            Check("and neither writes nor backs anything up",
                  File.ReadAllBytes(settings).SequenceEqual(after) && File.GetLastWriteTimeUtc(settings) == stamp
                  && Directory.GetFiles(claude, "settings.json.bak-*").Length == 1);

            var forced = await RunInstallHooks(exe, claude, vault, null, "--upgrade");
            Check("`--upgrade` with nothing to change exits 0 and leaves no backup behind",
                  forced.Exit == 0 && Directory.GetFiles(claude, "settings.json.bak-*").Length == 1, forced.Stdout + forced.Stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRAINX_CLAUDE_VERSION", old);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static async Task NoInstallerHooksNoUpgrade()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var root = TempDir("brainx-hook-none-");
        try
        {
            var claude = Path.Combine(root, "home", ".claude");
            var vault = Path.Combine(root, "vault");
            Directory.CreateDirectory(claude);
            Directory.CreateDirectory(vault);
            var settings = Path.Combine(claude, "settings.json");

            Check("no settings.json: not upgraded, and none is created",
                  !HookHealth.UpgradeInstallerHooks(claude, exe, vault) && !File.Exists(settings));

            var noneOfTheirs = new JObject
            {
                ["hooks"] = new JObject
                {
                    ["PreToolUse"] = new JArray(HookEntry(UserGuard, null)),
                    ["Stop"] = new JArray(new JObject { ["hooks"] = new JArray(ClaudeWakeHooks.Handler(exe, "hook-stop", vault, true)) }),
                },
            };
            File.WriteAllText(settings, noneOfTheirs.ToString(), new UTF8Encoding(false));
            var before = File.ReadAllBytes(settings);
            Check("the owner's hooks and a wake hook: not upgraded",
                  !HookHealth.UpgradeInstallerHooks(claude, exe, vault) && File.ReadAllBytes(settings).SequenceEqual(before));
            Check("and no script is dropped either", !Directory.Exists(Path.Combine(claude, "scripts")));

            // The installer's own guard, for whatever reaches it without the client's.
            var direct = await RunInstallHooks(exe, claude, vault, null, "--upgrade");
            Check("`--upgrade` run by hand on such a machine registers nothing",
                  direct.Exit == 0 && JToken.DeepEquals(JObject.Parse(File.ReadAllText(settings)), noneOfTheirs), direct.Stdout + direct.Stderr);

            File.WriteAllText(settings, "{ nope", new UTF8Encoding(false));
            Check("a settings.json that does not parse is left as it is",
                  !HookHealth.UpgradeInstallerHooks(claude, exe, vault) && File.ReadAllText(settings) == "{ nope");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static async Task UpgradeRespectsTheOwner()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var root = TempDir("brainx-hook-optout-");
        var old = Environment.GetEnvironmentVariable("BRAINX_CLAUDE_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("BRAINX_CLAUDE_VERSION", "2.1.296 (Claude Code)");
            var claude = Path.Combine(root, "home", ".claude");
            var vault = Path.Combine(root, "vault");
            Directory.CreateDirectory(vault);
            var settings = Path.Combine(claude, "settings.json");

            // Took out the prompt gate, the Stop nudge, one of the two PostToolUse
            // hooks and the auto-ingest. Never had PreCompact.
            var install = OldInstall(claude, exe);
            var hooks = (JObject)install["hooks"]!;
            hooks.Remove("UserPromptSubmit");
            hooks.Remove("PreCompact");
            var post = (JArray)hooks["PostToolUse"]!;
            post.Where(g => HookHealth.CommandText(g["hooks"]![0]).Contains("brain-error-recall.ps1")).ToList().ForEach(g => g.Remove());
            post.Where(g => HookHealth.IsAutoIngestHook(HookHealth.CommandText(g["hooks"]![0]))).ToList().ForEach(g => g.Remove());
            var stop = (JArray)hooks["Stop"]!;
            stop.Where(g => HookHealth.CommandText(g["hooks"]![0]).Contains("stop-hook.ps1")).ToList().ForEach(g => g.Remove());

            // A group the owner added a handler of theirs to.
            var pre = (JArray)hooks["PreToolUse"]!;
            ((JArray)pre[0]["hooks"]!).Add(new JObject { ["type"] = "command", ["command"] = "node C:/mine/shared-group.js" });
            File.WriteAllText(settings, install.ToString(), new UTF8Encoding(false));

            Check("the upgrade runs", HookHealth.UpgradeInstallerHooks(claude, exe, vault));
            var s = JObject.Parse(File.ReadAllText(settings));
            var text = s.ToString();
            Check("the hooks the owner took out stay out",
                  CountOf(s, "hook-prompt-gate") == 0 && CountOf(s, "hook-error-recall") == 0
                  && CountOf(s, "hook-brain-end") == 0 && CountOf(s, "hook-auto-ingest") == 0, text);
            Check("an event they never had is not created, not even empty",
                  s["hooks"]!["PreCompact"] == null && s["hooks"]!["UserPromptSubmit"] == null, text);
            Check("what they kept is upgraded", CountOf(s, "hook-pretool-warn") == 1 && CountOf(s, "hook-tool-log") == 1, text);
            Check("a handler of theirs that shared a group with ours stays",
                  CountOf(s, "node C:/mine/shared-group.js") == 1 && CountOf(s, UserGuard) == 1, text);
            Check("and the marked entry beside it is gone from that group, not duplicated",
                  CountOf(s, "brain-pretool-warn.ps1") == 0, text);

            // A native auto-ingest says where to post, and the client wrote that.
            var ingest = new JObject
            {
                ["matcher"] = "Read",
                ["hooks"] = new JArray(new JObject
                {
                    ["type"] = "command", ["command"] = exe,
                    ["args"] = new JArray("hook-auto-ingest", "--vault", vault, "--ingest-url", "http://localhost:6123/api/brain/auto-ingest", "--tag", HookHealth.AutoIngestNativeTag),
                }),
            };
            var withIngest = JObject.Parse(File.ReadAllText(settings));
            ((JArray)withIngest["hooks"]!["PostToolUse"]!).Add(ingest);
            ((JArray)withIngest["hooks"]!["SessionEnd"]!).Clear();
            ((JArray)withIngest["hooks"]!["SessionEnd"]!).Add(HookEntry($"powershell -File \"x/session-end.ps1\" # brainx-hooks v2"));
            File.WriteAllText(settings, withIngest.ToString(), new UTF8Encoding(false));
            await RunInstallHooks(exe, claude, vault, null, "--upgrade");
            Check("a later upgrade keeps the port the client wrote",
                  File.ReadAllText(settings).Contains("http://localhost:6123/api/brain/auto-ingest", StringComparison.Ordinal), File.ReadAllText(settings));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRAINX_CLAUDE_VERSION", old);
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
