using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BrainX.Core.Services;
using BrainX.Core.Services.Chrome;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// Review rev3 of the Claude in Chrome bridge (2026-10-10).
///
/// The LOGIC finding: unattended codex broker runs still listed and called
/// chrome__*. The gate read BRAINX_BROKER_RUN from brainx-mcp's environment,
/// the broker set it on the runner only, and codex clears its MCP servers'
/// environment down to an allow-list plus each server's configured env — so
/// the default codex runner's brain never saw it. The checks here prove both
/// halves of the fix with a real broker, a stand-in codex.exe (this harness
/// under that name) and a brain started below it the way codex starts one:
/// with the variable stripped.
///
/// Plus the three smaller findings: the redactor's '/' and '#' delimiters
/// hiding SSH targets, the hook alarm pointing at a tail Claude Code never
/// shows, and the writer's queue bounded in entries but not in bytes.
/// </summary>
internal static partial class Program
{
    /// <summary>Set on a broker (or a runner) by the checks below: the runner
    /// this harness stands in for writes its report to this path.</summary>
    internal const string RunnerProbeEnv = "BRAINX_TEST_RUNNER_PROBE";

    private const string BrokerRunOverride = "mcp_servers.brainx-brain.env.BRAINX_BROKER_RUN=\"1\"";

    private static readonly string[] DefaultCodexRunnerArgs =
        { "exec", "--skip-git-repo-check", "--approve-for-me", "-C", "{cwd}", "{prompt}" };

    /// <summary>The owner's ~/.codex/config.toml, shape for shape (2026-10-10;
    /// names and keys as there, values invented): a stdio brain with its env
    /// table and per-tool tables, a stdio server that is not a brain, a url
    /// server, hooks whose command mentions brainx-mcp.</summary>
    private const string OwnerShapedCodexConfig = """
model = "gpt-6.1"
notify = ["powershell", "-File", "C:\\x\\notify.ps1"]

[windows]
sandbox = "elevated"

[projects.'d:\brainx']
trust_level = "trusted"

[mcp_servers.node_repl]
args = ["--stdio"]
command = "C:\\Program Files\\nodejs\\node.exe"
env_vars = ["PATH"]
startup_timeout_sec = 30

[mcp_servers.node_repl.env]
CODEX_HOME = "C:\\Users\\x\\.codex"

[mcp_servers.brainx-brain]
command = "C:\\Users\\x\\AppData\\Local\\xjanova\\brainx-mcp\\brainx-mcp.exe"

[mcp_servers.brainx-brain.env]
BRAINX_VAULT = "G:\\Vault"

[mcp_servers.brainx-brain.tools.brain_stats]
approval_mode = "approve"

[mcp_servers.unityMCP]
url = "http://localhost:8080/mcp"

[[hooks.Stop]]
[[hooks.Stop.hooks]]
type = "command"
command = "C:\\Users\\x\\AppData\\Local\\xjanova\\brainx-mcp\\brainx-mcp.exe hook stop"

[mcp_servers.Roblox_Studio]
args = ["--stdio"]
command = "C:\\Roblox\\StudioMCP.exe"
""";

    private static void RegisterUnattendedRunChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("chrome unattended: every runner the broker starts tells its brain — codex by config override, claude/gemini/grok by env", ChromeRunnerLaunchMarker));
        checks.Add(("chrome unattended: a brain below a recorded runner is unattended whatever its environment says", ChromeRegisteredRunnerHidesChrome));
        checks.Add(("chrome unattended: a codex broker run end to end — the override reaches codex, the registry covers its brain", ChromeBrokerCodexRunEndToEnd));
        checks.Add(("chrome redaction: URL fragments still masked, SSH paths and refs shown as typed", ChromeRedactionUrlScoped));
        checks.Add(("chrome hooks alarm: it names brain_stats → hooks, and that lists the problems", ChromeHookAlarmPointsAtStats));
        checks.Add(("chrome private log queue: bounded in bytes as well as in entries", ChromeQueueByteBudget));
    }

    // ───────────── 1. what each runner is started with ─────────────

    private static Task ChromeRunnerLaunchMarker()
    {
        Check("(control) the default codex runner's own args carry no BRAINX_BROKER_RUN — and codex passes its MCP servers only an allow-list plus their configured env",
              !DefaultCodexRunnerArgs.Any(a => a.Contains("BRAINX_BROKER_RUN", StringComparison.Ordinal)));

        var codex = UnattendedRuns.ForRunner(@"C:\Users\x\AppData\Local\OpenAI\Codex\bin\9691020b546a15b2\codex.exe",
                                             DefaultCodexRunnerArgs, OwnerShapedCodexConfig);
        Check("a codex runner gets -c mcp_servers.brainx-brain.env.BRAINX_BROKER_RUN=\"1\" right after `exec`, the rest unchanged",
              codex.Args.Take(3).SequenceEqual(new[] { "exec", "-c", BrokerRunOverride })
              && codex.Args.Skip(3).SequenceEqual(DefaultCodexRunnerArgs.Skip(1)), string.Join(" ", codex.Args));
        Check("…for the brain server only — not node_repl, not Roblox_Studio, not the url server unityMCP (codex refuses `env` on a url server), not the hook that mentions brainx-mcp",
              codex.CodexServers.SequenceEqual(new[] { "brainx-brain" }) && codex.Args.Count(a => a == "-c") == 1,
              string.Join(",", codex.CodexServers));
        Check("…and BRAINX_BROKER_RUN=1 in its environment as well",
              codex.Env.TryGetValue(UnattendedRuns.BrokerRunEnv, out var cv) && cv == "1");

        var shim = UnattendedRuns.ForRunner(@"C:\Users\x\AppData\Roaming\npm\codex.cmd", DefaultCodexRunnerArgs, OwnerShapedCodexConfig);
        Check("the npm shim codex.cmd is codex too", shim.Args.Contains(BrokerRunOverride), string.Join(" ", shim.Args));

        var renamed = UnattendedRuns.ForRunner("codex", new[] { "exec", "{prompt}" },
            "[mcp_servers.my-brain]\ncommand = 'D:\\BrainX\\brainx-mcp.exe'\nargs = []\n");
        Check("a brain registered under another name gets the override under THAT name",
              renamed.Args.SequenceEqual(new[] { "exec", "-c", "mcp_servers.my-brain.env.BRAINX_BROKER_RUN=\"1\"", "{prompt}" }),
              string.Join(" ", renamed.Args));

        var inline = UnattendedRuns.ForRunner("codex", new[] { "exec", "{prompt}" },
            "[mcp_servers]\nbrain = { command = \"node\", env = { BRAINX_VAULT = \"G:\\\\V\" } }\nother = { url = \"http://x/mcp\" }\n");
        Check("an inline server table under [mcp_servers] is read too (a BRAINX_ env makes it a brain; the url one is skipped)",
              inline.CodexServers.SequenceEqual(new[] { "brain" }), string.Join(",", inline.CodexServers));

        var none = UnattendedRuns.ForRunner("codex", DefaultCodexRunnerArgs,
            "[mcp_servers.unityMCP]\nurl = \"http://localhost:8080/mcp\"\n\n[mcp_servers.brainx-cloud]\nurl = \"https://brainx.example/mcp\"\n");
        Check("no stdio brain in codex's config → no override at all (one on a missing server leaves codex a server with no transport, which codex refuses); the registry covers such a run",
              none.Args.SequenceEqual(DefaultCodexRunnerArgs) && none.CodexServers.Count == 0, string.Join(" ", none.Args));
        Check("…nor with no config.toml", UnattendedRuns.ForRunner("codex", DefaultCodexRunnerArgs, null).Args.SequenceEqual(DefaultCodexRunnerArgs));

        var mindArgs = new[] { "exec", "--oss", "--local-provider", "ollama", "-c", "mcp_servers.brainx-brain.env.BRAINX_AS_AGENT=\"mind\"",
                               "-c", BrokerRunOverride, "--skip-git-repo-check", "-C", "{cwd}", "{prompt}" };
        var mind = UnattendedRuns.ForRunner("codex", mindArgs, OwnerShapedCodexConfig);
        Check("the Mind's runner already carries it: not added twice", mind.Args.SequenceEqual(mindArgs), string.Join(" ", mind.Args));

        var claudeArgs = new[] { "-p", "{prompt}", "--allowedTools", "mcp__brainx-brain" };
        var claude = UnattendedRuns.ForRunner(@"C:\Users\x\AppData\Roaming\Claude\claude-code\2.1.293\claude.exe", claudeArgs, OwnerShapedCodexConfig);
        Check("a claude runner: args untouched, BRAINX_BROKER_RUN=1 in its environment (Claude Code hands its stdio servers its whole environment)",
              claude.Args.SequenceEqual(claudeArgs) && claude.Env[UnattendedRuns.BrokerRunEnv] == "1");

        var grokArgs = new[] { "-p", "{prompt}", "--cwd", "{cwd}", "--always-approve" };
        var grok = UnattendedRuns.ForRunner(@"C:\Users\x\.grok\bin\grok.exe", grokArgs, OwnerShapedCodexConfig);
        Check("a grok runner: args untouched, env set (how Grok Build passes env to MCP servers is not read — the run registry covers it either way)",
              grok.Args.SequenceEqual(grokArgs) && grok.Env[UnattendedRuns.BrokerRunEnv] == "1");

        // gemini-cli 0.55.1 (bundle: sanitizeEnvironment with redaction on for
        // MCP servers) hands a stdio server the CLI's environment minus names
        // matching these patterns.
        var geminiDrops = new Regex("TOKEN|SECRET|PASSWORD|PASSWD|KEY|AUTH|CREDENTIAL|CREDS|PRIVATE|CERT", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var geminiArgs = new[] { "-p", "{prompt}", "--yolo" };
        var gemini = UnattendedRuns.ForRunner(@"C:\Users\x\AppData\Roaming\npm\gemini.cmd", geminiArgs, OwnerShapedCodexConfig);
        Check("a gemini runner: args untouched, env set, and the name passes gemini-cli's MCP env filter (no TOKEN/KEY/AUTH/… in it)",
              gemini.Args.SequenceEqual(geminiArgs) && gemini.Env[UnattendedRuns.BrokerRunEnv] == "1" && !geminiDrops.IsMatch(UnattendedRuns.BrokerRunEnv));
        Check("(control) that filter is real: it would drop BRAINX_PRIVATE_DIR", geminiDrops.IsMatch("BRAINX_PRIVATE_DIR"));
        return Task.CompletedTask;
    }

    // ───────────── 2. the run registry ─────────────

    /// <summary>
    /// The harness records ITSELF as a runner and starts a brain with the
    /// environment codex would give it (no BRAINX_BROKER_RUN, no
    /// BRAINX_AS_AGENT). Its only way to know is the registry.
    /// </summary>
    private static async Task ChromeRegisteredRunnerHidesChrome()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        var me = Environment.ProcessId;
        var created = UnattendedRuns.CreationTime(me);
        Check("(setup) this harness's creation time can be read — it stands in for the runner", created != null);
        if (created == null) return;

        var (root, vault, priv) = ChromeSandboxDirs("brainx-chrome-reg-");
        var runs = Path.Combine(priv, "unattended-runs");
        new PrivateBrowseStore(priv).Record(new PrivateBrowseRecord { Tool = "get_page_text", ResultText = "QREG1 bank page" });
        var bus = Path.Combine(vault, ".obsidianx", "agent-bus");
        Directory.CreateDirectory(Path.Combine(bus, "cowork", "messages"));
        SetRoomLight(bus, on: true);
        var m = "R" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var started = Path.Combine(root, "stub-started.txt");
        var stubLog = Path.Combine(root, "stub-requests.log");
        var url = ChromeStubServer.MarkerUrl(m);
        // What codex hands a brain: neither variable.
        var codexEnv = StubEnv(m, root, new()
        {
            [UnattendedRuns.BrokerRunEnv] = null, [UnattendedRuns.AsAgentEnv] = null, [ChromeStubServer.LogEnv] = stubLog,
        });
        static List<string> Names(JObject? list) =>
            (list?["result"]?["tools"] as JArray ?? new JArray()).Select(t => t["name"]!.ToString()).ToList();

        ChromeSession? u = null, c = null;
        try
        {
            Check("(setup) the harness is recorded as a runner — pid and creation time",
                  UnattendedRuns.Register(me, created.Value, "codex", runs) && UnattendedRuns.IsRegistered(me, created.Value, runs));

            u = await StartChromeSession(exe, vault, priv, codexEnv);
            var names = Names(await u.List());
            Check("below a recorded runner, with no BRAINX_BROKER_RUN in its environment: no chrome__ tool, no private_browse_search",
                  names.Count > 0 && !names.Any(n => n.StartsWith("chrome_", StringComparison.OrdinalIgnoreCase)) && !names.Contains("private_browse_search"),
                  string.Join(",", names.Where(n => n.StartsWith("chrome_") || n.StartsWith("private_"))));
            var nav = await u.Call("chrome__navigate", new JObject { ["url"] = url, ["tabId"] = 1 });
            var search = await u.Call("private_browse_search", new JObject { ["query"] = "QREG1" });
            Check("…chrome__navigate by name is refused as an unattended run (en + th); the private log is not read",
                  IsError(nav) && ResultText(nav).Contains("unattended runs") && ResultText(nav).Contains("ไม่มีคนดู")
                  && IsError(search) && !ResultText(search).Contains("bank page"), ResultText(nav) + " | " + ResultText(search));
            var st = await u.Status();
            Check("…bridge_status: Disabled, unattended, no settings action",
                  st?["state"]?.ToString() == "Disabled" && st["unattended"]?.Type == JTokenType.Boolean && (bool)st["unattended"]!
                  && st["action"]?["kind"]?.ToString() == "none", st?.ToString(Newtonsoft.Json.Formatting.None));
            await u.Call("cowork_join", new JObject());
            var left = await u.Call("cowork_leave", new JObject());
            JObject? leftBody = null;
            try { leftBody = JObject.Parse(ResultText(left)); } catch { }
            Check("…and its cowork_leave leaves for this session only, as a broker run's does — the agent's seat stays",
                  leftBody?["sessionOnly"]?.Type == JTokenType.Boolean && (bool)leftBody["sessionOnly"]!
                  && leftBody["note"]?.ToString().Contains("broker run") == true, ResultText(left));
            await u.DisposeAsync();
            u = null;
            Check("…and nothing was spawned: the stub never started, not even for discovery",
                  !File.Exists(started) && ReadStubLog(stubLog).Count == 0, $"started={File.Exists(started)}, requests={ReadStubLog(stubLog).Count}");

            // Control: the same pid with another creation time — exactly what a
            // reused pid looks like. It must not count.
            UnattendedRuns.Unregister(me, runs);
            Check("(setup) re-recorded with a creation time one tick off",
                  UnattendedRuns.Register(me, created.Value - 1, "codex", runs) && !UnattendedRuns.IsRegistered(me, created.Value, runs));
            c = await StartChromeSession(exe, vault, priv, codexEnv);
            var cNames = Names(await c.List());
            var cNav = await c.Call("chrome__navigate", new JObject { ["url"] = url, ["tabId"] = 1 });
            Check("(control) a recorded pid with another creation time (a reused pid) does not count: chrome__navigate is listed and reaches the stub",
                  cNames.Contains("chrome__navigate") && !IsError(cNav) && ResultText(cNav).Contains("Navigated to " + url)
                  && File.Exists(started) && ReadStubLog(stubLog).Any(h => h.Method == "tools/call" && h.Tool == "navigate"), ResultText(cNav));
            await c.Call("cowork_join", new JObject());
            var cLeft = await c.Call("cowork_leave", new JObject());
            JObject? cBody = null;
            try { cBody = JObject.Parse(ResultText(cLeft)); } catch { }
            Check("(control) …and its cowork_leave is an attended session's: the seat is closed, not just the session",
                  cBody != null && cBody["sessionOnly"] == null, ResultText(cLeft));
        }
        finally
        {
            if (u != null) await u.DisposeAsync();
            if (c != null) await c.DisposeAsync();
            UnattendedRuns.Unregister(me, runs);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ───────────── 3. a real broker, a stand-in codex.exe ─────────────

    /// <summary>
    /// A sandboxed broker follows up a quiet card and starts the default codex
    /// runner. The runner is this harness under the name codex.exe (an apphost
    /// copy beside BrainX.Tests.dll): it records the argv it got, then starts a
    /// brain the way codex does — BRAINX_BROKER_RUN and BRAINX_AS_AGENT not
    /// passed on — and asks it for tools and a chrome__navigate.
    /// </summary>
    private static async Task ChromeBrokerCodexRunEndToEnd()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }
        var self = Environment.ProcessPath;
        if (self == null || !Path.GetFileName(self).Equals("BrainX.Tests.exe", StringComparison.OrdinalIgnoreCase))
        {
            Check("(setup) the harness runs as BrainX.Tests.exe — a copy of it stands in for codex.exe", false, self);
            return;
        }
        var codexExe = Path.Combine(Path.GetDirectoryName(self)!, "codex.exe");

        var (root, vault, priv) = ChromeSandboxDirs("brainx-chrome-broker-");
        var runs = Path.Combine(priv, "unattended-runs");
        var key = Path.Combine(root, "bus-seal.key");
        var bus = Path.Combine(vault, ".obsidianx", "agent-bus");
        var tasks = Path.Combine(bus, "cowork", "tasks");
        Directory.CreateDirectory(tasks);
        Directory.CreateDirectory(Path.Combine(bus, "cowork", "messages"));
        Directory.CreateDirectory(Path.Combine(bus, "broker"));
        SetRoomLight(bus, on: true);
        var codexHome = Path.Combine(root, "codex-home");
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "config.toml"), OwnerShapedCodexConfig, new UTF8Encoding(false));

        var m = "B" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var started = Path.Combine(root, "stub-started.txt");
        var stubLog = Path.Combine(root, "stub-requests.log");
        var url = ChromeStubServer.MarkerUrl(m);

        File.WriteAllText(Path.Combine(bus, "runners.json"), new JObject
        {
            ["pollSeconds"] = 5,
            ["idleStudy"] = false,
            ["workRoots"] = new JArray(),
            ["escalation"] = new JObject { ["toast"] = false, ["chatCard"] = false, ["telegram"] = new JObject { ["botToken"] = "", ["chatId"] = "" } },
            ["runners"] = new JObject
            {
                ["codex"] = new JObject { ["exe"] = codexExe, ["args"] = new JArray(DefaultCodexRunnerArgs), ["cwd"] = root, ["onCall"] = true },
            },
        }.ToString(), new UTF8Encoding(false));
        // Quiet unfinished work on codex's card: the broker follows it up and calls codex.
        File.WriteAllText(Path.Combine(tasks, "t-chr001.json"), new JObject
        {
            ["id"] = "t-chr001", ["title"] = "check the dashboard numbers", ["status"] = "doing", ["assignee"] = "codex",
            ["createdBy"] = "owner", ["work"] = "isle-art",
            ["createdUtc"] = DateTime.UtcNow.AddHours(-2).ToString("o"), ["updatedUtc"] = DateTime.UtcNow.AddMinutes(-40).ToString("o"),
        }.ToString(), new UTF8Encoding(false));

        void SandboxEnv(ProcessStartInfo psi, string probeOut)
        {
            psi.Environment["BRAINX_BUS_SEAL_KEY"] = key;
            psi.Environment["BRAINX_SANDBOX"] = "1";
            psi.Environment["BRAINX_CANARY"] = "0";
            psi.Environment["BRAINX_PRIVATE_DIR"] = priv;
            psi.Environment["CODEX_HOME"] = codexHome;
            psi.Environment["BRAINX_TEST_MCP_EXE"] = exe;
            psi.Environment[RunnerProbeEnv] = probeOut;
            psi.Environment.Remove(StubMcpServer.EnvFlag);
            psi.Environment.Remove(UnattendedRuns.BrokerRunEnv);
            psi.Environment.Remove(UnattendedRuns.AsAgentEnv);
            psi.Environment.Remove("CLAUDECODE");
            psi.Environment.Remove("CLAUDE_CODE_ENTRYPOINT");
            foreach (var (k, v) in StubEnv(m, root, new() { [ChromeStubServer.LogEnv] = stubLog }))
                if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v;
        }

        static async Task<string> RunToEnd(ProcessStartInfo psi)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } return "(timed out)"; }
            return await outTask;
        }
        static JObject? ReadProbe(string path)
        {
            try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null; } catch { return null; }
        }

        try
        {
            File.Copy(self, codexExe, overwrite: true);

            var brokerProbe = Path.Combine(root, "probe-broker.json");
            var brokerPsi = new ProcessStartInfo(exe, $"broker --vault \"{vault}\" --once");
            SandboxEnv(brokerPsi, brokerProbe);
            var brokerOut = await RunToEnd(brokerPsi);
            var probe = ReadProbe(brokerProbe);
            Check("(setup) the broker called codex for its quiet card — this harness as codex.exe — and the runner reported back",
                  brokerOut.Contains("codex: spawned codex.exe") && probe != null && probe["error"] == null && probe["processName"]?.ToString() == "codex",
                  probe?["error"]?.ToString() ?? brokerOut);
            if (probe == null) return;

            var argv = (probe["argv"] as JArray ?? new JArray()).Select(a => a.ToString()).ToList();
            Check("codex was started with -c mcp_servers.brainx-brain.env.BRAINX_BROKER_RUN=\"1\" right after exec — the string codex parses, quotes intact",
                  argv.Take(3).SequenceEqual(new[] { "exec", "-c", BrokerRunOverride }), string.Join(" ‖ ", argv.Take(6)));
            Check("…and no override for unityMCP (url), node_repl or Roblox_Studio",
                  argv.Count(a => a.StartsWith("mcp_servers.", StringComparison.Ordinal)) == 1, string.Join(" ‖ ", argv.Where(a => a.StartsWith("mcp_servers."))));
            Check("…with BRAINX_BROKER_RUN=1 in the runner's own environment", probe["brokerRunEnv"]?.ToString() == "1");
            Check("the broker recorded the runner (pid + creation time) before the runner could start a brain",
                  probe["registered"]?.Type == JTokenType.Boolean && (bool)probe["registered"]!);

            var tools = (probe["tools"] as JArray ?? new JArray()).Select(t => t.ToString()).ToList();
            Check("a brain the runner started the way codex starts one — BRAINX_BROKER_RUN and BRAINX_AS_AGENT not passed on — lists no chrome__ tool and no private_browse_search",
                  tools.Count > 0 && !tools.Any(n => n.StartsWith("chrome_", StringComparison.OrdinalIgnoreCase)) && !tools.Contains("private_browse_search"),
                  string.Join(",", tools.Where(n => n.StartsWith("chrome_") || n.StartsWith("private_"))));
            Check("…refuses chrome__navigate as an unattended run, en + th",
                  probe["navigateError"]?.Type == JTokenType.Boolean && (bool)probe["navigateError"]!
                  && probe["navigateText"]?.ToString().Contains("unattended runs") == true && probe["navigateText"]?.ToString().Contains("ไม่มีคนดู") == true,
                  probe["navigateText"]?.ToString());
            Check("…and started nothing: no discovery, no stub",
                  !File.Exists(started) && ReadStubLog(stubLog).Count == 0, $"started={File.Exists(started)}, requests={ReadStubLog(stubLog).Count}");
            var pid = probe["pid"]?.ToObject<int?>() ?? -1;
            Check("once the broker reaped the run, its entry is gone from the registry",
                  pid > 0 && !UnattendedRuns.Entries(runs).Any(e => e.Pid == pid), string.Join(",", UnattendedRuns.Entries(runs)));

            // Control: the very same runner and environment, started by the
            // harness instead of the broker — so not recorded. The brain below
            // it gets the same stripped environment.
            var directProbe = Path.Combine(root, "probe-direct.json");
            var direct = new ProcessStartInfo(codexExe);
            foreach (var a in DefaultCodexRunnerArgs) direct.ArgumentList.Add(a.Replace("{cwd}", root).Replace("{prompt}", "control"));
            SandboxEnv(direct, directProbe);
            direct.Environment["BRAINX_VAULT"] = vault;
            direct.Environment[UnattendedRuns.BrokerRunEnv] = "1";   // what the broker puts on a runner; the brain still never sees it
            await RunToEnd(direct);
            var ctl = ReadProbe(directProbe);
            var ctlTools = (ctl?["tools"] as JArray ?? new JArray()).Select(t => t.ToString()).ToList();
            Check("(control) the same runner started outside the broker is not recorded, and the brain below it — same stripped environment — lists chrome__navigate and its call reaches the stub",
                  ctl != null && ctl["registered"]?.Type == JTokenType.Boolean && !(bool)ctl["registered"]!
                  && ctlTools.Contains("chrome__navigate") && ctl["navigateError"]?.Type == JTokenType.Boolean && !(bool)ctl["navigateError"]!
                  && ctl["navigateText"]?.ToString().Contains("Navigated to " + url) == true && File.Exists(started),
                  ctl?["error"]?.ToString() ?? ctl?["navigateText"]?.ToString());
        }
        finally
        {
            for (var i = 0; i < 20 && File.Exists(codexExe); i++)
            {
                try { File.Delete(codexExe); } catch { await Task.Delay(250); }
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The stand-in runner (this exe as codex.exe). Writes what it was started
    /// with, whether the broker recorded it, and what a brain started below it
    /// with codex's kind of environment offers. Always exits 0.
    /// </summary>
    private static async Task<int> RunnerProbeAsync(string outPath)
    {
        var o = new JObject
        {
            ["pid"] = Environment.ProcessId,
            ["processName"] = Process.GetCurrentProcess().ProcessName,
            ["argv"] = new JArray(Environment.GetCommandLineArgs().Skip(1)),
            ["brokerRunEnv"] = Environment.GetEnvironmentVariable(UnattendedRuns.BrokerRunEnv),
        };
        try
        {
            var created = UnattendedRuns.CreationTime(Environment.ProcessId);
            var registered = false;
            for (var i = 0; i < 30 && created != null; i++)
            {
                if (registered = UnattendedRuns.IsRegistered(Environment.ProcessId, created.Value)) break;
                await Task.Delay(100);
            }
            o["registered"] = registered;

            var exe = FindMcpExe() ?? throw new InvalidOperationException("no brainx-mcp.exe");
            var vault = Environment.GetEnvironmentVariable("BRAINX_VAULT") ?? throw new InvalidOperationException("no BRAINX_VAULT");
            var marker = Environment.GetEnvironmentVariable(ChromeStubServer.MarkerEnv) ?? "NOMARKER";
            var s = await StartChromeSession(exe, vault, ChromeBridgeSettings.PrivateRoot, new()
            {
                // codex passes neither on (allow-list + configured env only).
                [UnattendedRuns.BrokerRunEnv] = null,
                [UnattendedRuns.AsAgentEnv] = null,
                [RunnerProbeEnv] = null,
            });
            try
            {
                var names = (((await s.List())?["result"]?["tools"]) as JArray ?? new JArray()).Select(t => t["name"]!.ToString()).ToList();
                var nav = await s.Call("chrome__navigate", new JObject { ["url"] = ChromeStubServer.MarkerUrl(marker), ["tabId"] = 1 });
                o["tools"] = new JArray(names);
                o["navigateError"] = IsError(nav);
                o["navigateText"] = ResultText(nav);
            }
            finally { await s.DisposeAsync(); }
        }
        catch (Exception ex) { o["error"] = ex.ToString(); }
        File.WriteAllText(outPath, o.ToString(), new UTF8Encoding(false));
        return 0;
    }

    // ───────────── 4. the redactor: URL-scoped, SSH text as typed ─────────────

    private static Task ChromeRedactionUrlScoped()
    {
        const string names = @"[a-z0-9_.\-]*(?:pass(?:word|wd|phrase)?|pwd|secret|token|api[_\-]?key|apikey|access[_\-]?key|private[_\-]?key|client[_\-]?secret|auth(?:[_\-]?key)?|credentials?)[a-z0-9_.\-]*";
        const string value = @"(""[^""]*""|'[^']*'|[^\s&;'""]+)";
        // The rule as review rev2 left it ('#' and '/' lead a secret everywhere)
        // and the shell rule alone, without the URL pass.
        var rev2 = new Regex(@"((?:^|[\s;&|""'?,({#/])-{0,2}" + names + @"\s*[=:]\s*)" + value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var shellOnly = new Regex(@"((?:^|[\s;&|""'?,({])-{0,2}" + names + @"\s*[=:]\s*)" + value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var cmd in new[]
                 {
                     "git push --force origin feature/auth:main",
                     "docker run -v /srv/secrets:/run/secrets app",
                     "vim /src/auth.ts:12",
                     "open /etc/app/secret.key: permission denied",
                 })
        {
            var then = rev2.Replace(cmd, "$1***");
            Check($"(control) rev2's rule masked it: {then}", then != cmd);
            Check($"shown as typed: {cmd}", SecretRedactor.Redact(cmd) == cmd, SecretRedactor.Redact(cmd));
        }

        // The SSH approval card itself.
        var dir = Path.Combine(Path.GetTempPath(), "brainx-approvals-redact-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SshApprovalStore(dir);
            var profile = new SshProfile { Id = "test-prod", Host = "203.0.113.10", User = "admin", AllowPatterns = ["^.+"] };
            const string push = "git push --force origin feature/auth:main";
            var (rec, _) = store.RequestOrReuse(profile, push, SshCommandRisk.Classify(push), "claude", null);
            Check("the SSH approval card shows the force push's target: command_display is the command", rec.CommandDisplay == push, rec.CommandDisplay);
            const string mount = "docker run -v /srv/secrets:/run/secrets --rm app";
            var (rec2, _) = store.RequestOrReuse(profile, mount, SshCommandRisk.Classify(mount), "claude", null);
            Check("…and a docker mount's target", rec2.CommandDisplay == mount, rec2.CommandDisplay);
            const string withPw = "mysql -uroot -pS3cr3tPass! -e 'select 1'";
            var (rec3, _) = store.RequestOrReuse(profile, withPw, SshCommandRisk.Classify(withPw), "claude", null);
            Check("(control) a real credential on the card is still masked", !rec3.CommandDisplay.Contains("S3cr3tPass!"), rec3.CommandDisplay);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }

        var opaque = "ya29.A0ARrdaMx9Qz7Lk2Wm4Np6Rt8Vb0Cd3Fg5Hj";
        var implicitUrl = $"https://oauth.example/oauthcb#access_token={opaque}&token_type=Bearer&expires_in=3599";
        Check("(control) the shell rule alone keeps the opaque #access_token= — only the URL pass masks it",
              shellOnly.Replace(implicitUrl, "$1***").Contains(opaque));
        Check("an opaque #access_token= in a URL is still masked",
              !SecretRedactor.Redact(implicitUrl).Contains(opaque) && SecretRedactor.Redact(implicitUrl).Contains("#access_token=***"), SecretRedactor.Redact(implicitUrl));
        Check("…in an SSH command too (curl to the callback)", !SecretRedactor.Redact($"curl -s '{implicitUrl}'").Contains(opaque));
        Check("…and in a /session_token= path segment and a ;sessiontoken= path parameter",
              !SecretRedactor.Redact($"https://app.example/cb/session_token={opaque}/next").Contains(opaque)
              && !SecretRedactor.Redact($"https://app.example/cb;sessiontoken={opaque}").Contains(opaque));
        const string refUrl = "https://git.example:8443/org/repo/compare/feature/auth:main#install";
        Check("…while a URL's port, a ref in its path and a plain fragment are left alone", SecretRedactor.Redact(refUrl) == refUrl, SecretRedactor.Redact(refUrl));

        // A page of minified script: dotted identifiers with no spaces. The URL
        // pass must not make it "too complex to redact" (250 ms budget).
        var js = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"a{i}.b.c(")) + " https://x.example/cb#token=abc";
        var sw = Stopwatch.StartNew();
        var redJs = SecretRedactor.Redact(js);
        Check($"a 200 K page of dotted script is redacted, not withheld, in {sw.ElapsedMilliseconds} ms",
              !redJs.StartsWith("[withheld") && redJs.EndsWith("#token=***"), redJs.Length > 80 ? redJs[^80..] : redJs);
        return Task.CompletedTask;
    }

    // ───────────── 5. the hook alarm points somewhere a session can read ─────────────

    private static async Task ChromeHookAlarmPointsAtStats()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        // Bridge on, as on the owner's PC: the privacy clause sits in the head too.
        var (root, vault, priv) = ChromeSandboxDirs("brainx-chrome-hooks-");
        var claudeDir = Path.Combine(root, "claude");
        Directory.CreateDirectory(claudeDir);
        var hooks = new JArray();
        foreach (var script in new[] { "brain-tool-logger.ps1", "brain-pretool-warn.ps1", "brain-error-recall.ps1", "brain-session-end.ps1" })
            hooks.Add(new JObject
            {
                ["type"] = "command",
                ["command"] = $"powershell -NoProfile -File \"{Path.Combine(root, "gone", script)}\" -Home $env:USERPROFILE # brainx-hooks",
            });
        File.WriteAllText(Path.Combine(claudeDir, "settings.json"),
            new JObject { ["hooks"] = new JObject { ["PostToolUse"] = new JArray(new JObject { ["hooks"] = hooks }) } }.ToString());
        var problems = HookHealth.Problems(claudeDir, TimeSpan.FromHours(48));

        ChromeSession? s = null, healthy = null;
        try
        {
            s = await StartChromeSession(exe, vault, priv, StubEnv("HK1", root, new() { ["BRAINX_SANDBOX_CLAUDE_DIR"] = claudeDir }));
            var ins = s.Instructions;
            var rulesEnd = ins.IndexOf("Each tool's description carries its full protocol.", StringComparison.Ordinal);
            var alarmAt = ins.IndexOf(HookHealth.HeadAlarm, StringComparison.Ordinal);
            Check("the head's hook alarm says where the list is — brain_stats → hooks — and no longer promises details at the end",
                  problems.Count >= 2 && alarmAt >= 0 && alarmAt < rulesEnd && HookHealth.HeadAlarm.Contains("brain_stats → hooks")
                  && !ins.Contains("details at the end"), ins[..Math.Min(ins.Length, 300)]);
            var last = problems[^1];
            var lastEnd = rulesEnd < 0 ? -1 : ins.IndexOf(last, rulesEnd, StringComparison.Ordinal) + last.Length;
            Check($"(control) the tail is no place to send the agent: the last problem ends at {lastEnd}, past the 2,048 characters Claude Code shows",
                  lastEnd > 2048);

            var noIndex = await s.Call("brain_stats");
            Check("brain_stats with no index yet still names every problem and the fix",
                  IsError(noIndex) && problems.All(p => ResultText(noIndex).Contains(p)) && ResultText(noIndex).Contains(HookHealth.FixCommand),
                  ResultText(noIndex));

            var graph = new KnowledgeIndexer().IndexVault(vault);
            File.WriteAllText(Path.Combine(vault, ".obsidianx", "brain-export.json"), Newtonsoft.Json.JsonConvert.SerializeObject(
                BrainExporter.BuildExport(new BrainX.Core.Models.BrainIdentity { Address = "t", DisplayName = "t" }, graph, vault)));
            JObject? stats = null;
            try { stats = JObject.Parse(ResultText(await s.Call("brain_stats"))); } catch { }
            var h = stats?["hooks"] as JObject;
            Check("brain_stats → hooks: checked, not ok, exactly the problems, and the fix",
                  h != null && (bool?)h["checked"] == true && (bool?)h["ok"] == false
                  && (h["problems"] as JArray)?.Select(p => p.ToString()).SequenceEqual(problems) == true
                  && h["fix"]?.ToString() == HookHealth.FixCommand, h?.ToString(Newtonsoft.Json.Formatting.None));
            Check("…with what to tell the owner in English and Thai",
                  h?["tell"]?.ToString() is { } tell && tell.Contains(HookHealth.FixCommand) && tell.Contains("บอกเจ้าของ")
                  && h["effect"]?.ToString().Contains("เงียบ") == true, h?["tell"]?.ToString());

            healthy = await StartChromeSession(exe, vault, priv, StubEnv("HK2", root));
            JObject? hs = null;
            try { hs = JObject.Parse(ResultText(await healthy.Call("brain_stats")))["hooks"] as JObject; } catch { }
            Check("(control) a session whose hooks were not looked at (sandbox, no fake Claude folder): checked:false, no problems, no alarm",
                  hs != null && (bool?)hs["checked"] == false && hs["problems"] == null && !healthy.Instructions.Contains("HOOKS BROKEN"),
                  hs?.ToString(Newtonsoft.Json.Formatting.None));
        }
        finally
        {
            if (s != null) await s.DisposeAsync();
            if (healthy != null) await healthy.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ───────────── 6. the writer's queue, bounded in bytes ─────────────

    private static Task ChromeQueueByteBudget()
    {
        const int mb = 1024 * 1024;
        static PrivateBrowseRecord Shot(int i, int bytes)
        {
            var r = new PrivateBrowseRecord { Tool = "screenshot", ResultText = "Q" + i };
            r.Images.Add(("image/png", new byte[bytes]));
            return r;
        }
        static List<string> Drain(PrivateBrowseQueue q)
        {
            var list = new List<string>();
            while (q.TryDequeue(out var r)) list.Add(r.ResultText ?? "");
            return list;
        }

        var countOnly = new PrivateBrowseQueue(PrivateBrowseQueue.DefaultMaxEntries, long.MaxValue);
        for (var i = 0; i < 12; i++) countOnly.Enqueue(Shot(i, mb));
        Check($"(control) bounded by entries alone, twelve 1 MB screenshots are all held ({countOnly.PendingBytes / mb} MB) — at 64 entries of 8 × 15 MB that is 7.5 GB",
              countOnly.Count == 12 && countOnly.PendingBytes >= 12L * mb);

        var q = new PrivateBrowseQueue(PrivateBrowseQueue.DefaultMaxEntries, 8L * mb);
        var dropped = 0;
        for (var i = 0; i < 12; i++) dropped += q.Enqueue(Shot(i, mb));
        var held = q.PendingBytes;
        var kept = Drain(q);
        Check($"bounded in bytes too: an 8 MB budget holds {held / 1024} KB — the newest seven; the oldest five are dropped and counted",
              held <= 8L * mb && kept.SequenceEqual(Enumerable.Range(5, 7).Select(i => "Q" + i)) && dropped == 5 && q.Dropped == 5,
              string.Join(",", kept));

        var big = new PrivateBrowseQueue(PrivateBrowseQueue.DefaultMaxEntries, 8L * mb);
        big.Enqueue(Shot(0, mb));
        var bigDropped = big.Enqueue(Shot(1, 12 * mb));
        Check("one entry larger than the whole budget is kept on its own (the older one goes) — the call still reaches the store",
              bigDropped == 1 && Drain(big).SequenceEqual(new[] { "Q1" }));

        var many = new PrivateBrowseQueue();
        for (var i = 0; i < 100; i++) many.Enqueue(new PrivateBrowseRecord { Tool = "navigate", ResultText = "small" });
        Check("the 64-entry bound still holds for small entries", many.Count == 64 && many.Dropped == 36);

        // The writer in brainx-mcp actually queues through it, at these bounds.
        var exe = FindMcpExe();
        PrivateBrowseQueue? pending = null;
        try
        {
            var t = Assembly.LoadFrom(Path.ChangeExtension(exe!, ".dll")).GetType("BrainX.Mcp.Bridge.ChromeBridge");
            pending = t?.GetField("Pending", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as PrivateBrowseQueue;
        }
        catch { }
        Check("brainx-mcp's private log writer queues through it, at 64 entries and 64 MB",
              pending != null && pending.MaxEntries == 64 && pending.MaxBytes == 64L * mb);
        return Task.CompletedTask;
    }
}
