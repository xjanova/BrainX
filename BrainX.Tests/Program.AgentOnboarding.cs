using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

// Zero-touch onboarding ran once, at client start, and only Claude and Codex
// got brain-first rules; Gemini CLI and Grok Build were only ever healed,
// never added. These checks hold the pieces that closed those gaps: the rules
// installers for Gemini/Grok, the Gemini/Grok registrars, and the watcher that
// onboards an agent which appears while BrainX is already running.
internal static partial class Program
{
    private static void RegisterAgentOnboardingChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("agent rules: Gemini/Grok get a versioned block, owner text kept, absent agent untouched", AgentRulesSpliceAndSkip));
        checks.Add(("agent rules: Codex still splices the same way after the shared-block refactor", CodexRulesStillSplice));
        checks.Add(("gemini register: merge keeps owner keys, sweeps stale keys, refuses a broken file", GeminiRegistrationMerges));
        checks.Add(("grok register: a missing entry is appended once, an existing one is never touched", GrokRegistrationAppendsOnce));
        checks.Add(("mcp resolve: nothing found falls back to the stable mirror, never past it", McpResolveFallsBackToMirror));
        checks.Add(("agent watcher: baseline is quiet, an arrival fires once, self-made folders are not arrivals", AgentWatcherFiresOnArrival));
    }

    private static Task AgentRulesSpliceAndSkip()
    {
        var root = TempDir("brainx-agentrules-");
        try
        {
            var vault = Path.Combine(root, "vault"); Directory.CreateDirectory(vault);
            var gemini = Path.Combine(root, ".gemini"); Directory.CreateDirectory(gemini);
            var grok = Path.Combine(root, ".grok");

            // Grok is not installed: nothing is written and no folder appears.
            Check("absent Grok home is skipped", AgentRulesInstaller.EnsureGrok(vault, grok) == AgentRulesInstaller.InstallResult.SkippedNoAgent);
            Check("…and no .grok folder is fabricated", !Directory.Exists(grok));
            Check("a missing vault is skipped", AgentRulesInstaller.EnsureGemini(Path.Combine(root, "nope"), gemini) == AgentRulesInstaller.InstallResult.SkippedNoVault);

            var file = Path.Combine(gemini, "GEMINI.md");
            File.WriteAllText(file, "# My own Gemini notes\n\nAlways use tabs.\n");
            Check("first run installs", AgentRulesInstaller.EnsureGemini(vault, gemini) == AgentRulesInstaller.InstallResult.Installed);
            var once = File.ReadAllText(file);
            Check("owner text is kept", once.StartsWith("# My own Gemini notes\n\nAlways use tabs.", StringComparison.Ordinal), once);
            Check("the block names the vault and says to search first",
                  once.Contains(vault) && once.Contains("brain_search") && once.Contains(ManagedRulesBlock.BeginMarker), once);
            Check($"the block carries brainx-gemini-rules v{AgentRulesInstaller.RuleVersion}",
                  once.Contains($"<!-- brainx-gemini-rules v{AgentRulesInstaller.RuleVersion} -->"));

            Check("second run is a no-op", AgentRulesInstaller.EnsureGemini(vault, gemini) == AgentRulesInstaller.InstallResult.AlreadyCurrent);
            Check("…and the file is byte-identical", File.ReadAllText(file) == once);

            // An older block between text the owner wrote above AND below it.
            File.WriteAllText(file, "above\n\n" + ManagedRulesBlock.BeginMarker + "\n<!-- brainx-gemini-rules v0.1 -->\nold body\n"
                                    + ManagedRulesBlock.EndMarker + "\n\n\n\nbelow\n");
            Check("an old version upgrades", AgentRulesInstaller.EnsureGemini(vault, gemini) == AgentRulesInstaller.InstallResult.Installed);
            var up = File.ReadAllText(file);
            Check("the old body is gone and both owner lines stay",
                  !up.Contains("old body") && up.StartsWith("above\n\n", StringComparison.Ordinal) && up.EndsWith(ManagedRulesBlock.EndMarker + "\n\nbelow\n", StringComparison.Ordinal), up);
            Check("exactly one managed block", up.Split(ManagedRulesBlock.BeginMarker).Length == 2, up);

            Directory.CreateDirectory(grok);
            Check("Grok installs once its home exists", AgentRulesInstaller.EnsureGrok(vault, grok) == AgentRulesInstaller.InstallResult.Installed);
            var g = Path.Combine(grok, "AGENTS.md");
            Check("…into ~/.grok/AGENTS.md with its own tag", File.Exists(g)
                  && File.ReadAllText(g).Contains($"<!-- brainx-grok-rules v{AgentRulesInstaller.RuleVersion} -->"));
            Check("…and mentions Grok Build", File.ReadAllText(g).Contains("Grok Build"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task CodexRulesStillSplice()
    {
        var root = TempDir("brainx-codexrules-");
        var prev = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            var vault = Path.Combine(root, "vault"); Directory.CreateDirectory(vault);
            var codex = Path.Combine(root, ".codex");
            Environment.SetEnvironmentVariable("CODEX_HOME", codex);
            File.WriteAllText(Path.Combine(vault, "AGENTS.md"), "owner line\n");

            Check("codex installs", CodexAgentsRulesInstaller.EnsureInstalled(vault) == CodexAgentsRulesInstaller.InstallResult.Installed);
            var v = File.ReadAllText(Path.Combine(vault, "AGENTS.md"));
            Check("vault AGENTS.md keeps the owner line and gets the v-tag",
                  v.StartsWith("owner line\n\n", StringComparison.Ordinal)
                  && v.Contains($"<!-- brainx-agents-rules v{CodexAgentsRulesInstaller.RuleVersion} -->"), v);
            Check("global AGENTS.md is written under CODEX_HOME", File.Exists(Path.Combine(codex, "AGENTS.md")));
            Check("second run is a no-op", CodexAgentsRulesInstaller.EnsureInstalled(vault) == CodexAgentsRulesInstaller.InstallResult.AlreadyCurrent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", prev);
            try { Directory.Delete(root, true); } catch { }
        }
        return Task.CompletedTask;
    }

    private static Task GeminiRegistrationMerges()
    {
        var root = TempDir("brainx-geminireg-");
        try
        {
            var exe = Path.Combine(root, "stable", "brainx-mcp.exe");
            var other = Path.Combine(root, "dev", "brainx-mcp.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!); File.WriteAllText(exe, "");
            Directory.CreateDirectory(Path.GetDirectoryName(other)!); File.WriteAllText(other, "");
            var vault = Path.Combine(root, "vault");
            var cfg = Path.Combine(root, ".gemini", "settings.json");

            Check("a missing settings.json is created", AgentMcpRegistration.EnsureGemini(cfg, exe, vault, "2.9.1") == AgentMcpRegistration.Result.Written);
            var j = JObject.Parse(File.ReadAllText(cfg));
            Check("…with command, vault and version",
                  (string?)j["mcpServers"]?["brainx-brain"]?["command"] == exe
                  && (string?)j["mcpServers"]?["brainx-brain"]?["env"]?["BRAINX_VAULT"] == vault
                  && (string?)j["mcpServers"]?["brainx-brain"]?["env"]?["BRAINX_MCP_VERSION"] == "2.9.1", j.ToString());
            Check("…and no BOM", File.ReadAllBytes(cfg)[0] == (byte)'{');
            Check("same call again is Unchanged", AgentMcpRegistration.EnsureGemini(cfg, exe, vault, "2.9.1") == AgentMcpRegistration.Result.Unchanged);

            // The owner's own settings, another server, a stale suffixed key,
            // and an extra field on our entry.
            File.WriteAllText(cfg, new JObject
            {
                ["theme"] = "Dracula",
                ["mcpServers"] = new JObject
                {
                    ["github"] = new JObject { ["command"] = "gh-mcp" },
                    ["brainx-brain-v2"] = new JObject { ["command"] = "old.exe" },
                    ["brainx-brain"] = new JObject { ["command"] = other, ["timeout"] = 30000, ["env"] = new JObject { ["BRAINX_VAULT"] = vault } },
                },
            }.ToString());
            Check("onboarding keeps a working entry it may keep, but sweeps the stale key",
                  AgentMcpRegistration.EnsureGemini(cfg, exe, vault, "2.9.1", keepExisting: _ => true) == AgentMcpRegistration.Result.Written);
            j = JObject.Parse(File.ReadAllText(cfg));
            Check("…theme, github and timeout survive, command stays the dev build",
                  (string?)j["theme"] == "Dracula" && j["mcpServers"]?["github"] != null
                  && (int?)j["mcpServers"]?["brainx-brain"]?["timeout"] == 30000
                  && (string?)j["mcpServers"]?["brainx-brain"]?["command"] == other, j.ToString());
            Check("…brainx-brain-v2 is gone", j["mcpServers"]?["brainx-brain-v2"] == null, j.ToString());
            Check("…and a backup was left", File.Exists(cfg + ".brainx.bak"));

            Check("an outdated entry is repointed",
                  AgentMcpRegistration.EnsureGemini(cfg, exe, vault, "2.9.1", keepExisting: _ => false) == AgentMcpRegistration.Result.Written
                  && (string?)JObject.Parse(File.ReadAllText(cfg))["mcpServers"]?["brainx-brain"]?["command"] == exe);

            var wrongVault = Path.Combine(root, "other-vault");
            Check("a different vault is rewritten", AgentMcpRegistration.EnsureGemini(cfg, exe, wrongVault, "2.9.1") == AgentMcpRegistration.Result.Written
                  && (string?)JObject.Parse(File.ReadAllText(cfg))["mcpServers"]?["brainx-brain"]?["env"]?["BRAINX_VAULT"] == wrongVault);

            File.WriteAllText(cfg, "{ \"theme\": ");
            Check("a broken settings.json is refused", AgentMcpRegistration.EnsureGemini(cfg, exe, vault, "2.9.1") == AgentMcpRegistration.Result.Corrupt);
            Check("…and left exactly as it was", File.ReadAllText(cfg) == "{ \"theme\": ");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task GrokRegistrationAppendsOnce()
    {
        var root = TempDir("brainx-grokreg-");
        try
        {
            var exe = @"C:\Users\someone\.brainx\mcp\brainx-mcp.exe";
            var vault = @"G:\Obsidian";
            var cfg = Path.Combine(root, "config.toml");
            var owner = "[models]\ndefault = \"grok-4\"\n\n[mcp_servers.github]\ncommand = \"gh-mcp\"\n";
            File.WriteAllText(cfg, owner);

            Check("a config without our table gets one", AgentMcpRegistration.EnsureGrok(cfg, exe, vault, "2.9.1") == AgentMcpRegistration.Result.Written);
            var t = File.ReadAllText(cfg);
            Check("owner content is kept verbatim at the top", t.StartsWith(owner.TrimEnd('\n') + "\n\n", StringComparison.Ordinal), t);
            Check("the table uses literal strings for the Windows paths",
                  t.Contains("[mcp_servers.brainx-brain]\n") && t.Contains($"command = '{exe}'")
                  && t.Contains("[mcp_servers.brainx-brain.env]\n") && t.Contains($"BRAINX_VAULT = '{vault}'"), t);
            Check("second call is Unchanged", AgentMcpRegistration.EnsureGrok(cfg, exe, vault, "2.9.1") == AgentMcpRegistration.Result.Unchanged);
            Check("…and still exactly one table", t.Split("[mcp_servers.brainx-brain]").Length == 2 && File.ReadAllText(cfg) == t);

            foreach (var existing in new[]
                     {
                         "[mcp_servers.\"brainx-brain\"]\ncommand = 'x'\n",
                         "[mcp_servers]\nbrainx-brain = { command = 'x' }\n",
                         "  [ mcp_servers.brainx-brain.env ]\nBRAINX_VAULT = 'x'\n",
                     })
            {
                File.WriteAllText(cfg, existing);
                Check($"an existing entry is left alone: {existing.Split('\n')[0].Trim()}",
                      AgentMcpRegistration.EnsureGrok(cfg, exe, vault, null) == AgentMcpRegistration.Result.Unchanged && File.ReadAllText(cfg) == existing);
            }

            var fresh = Path.Combine(root, "sub", "config.toml");
            Check("no config yet: one is created", AgentMcpRegistration.EnsureGrok(fresh, exe, vault, null) == AgentMcpRegistration.Result.Written
                  && File.ReadAllText(fresh).StartsWith("# Added by BrainX", StringComparison.Ordinal));
            Check("…without a version line when none is known", !File.ReadAllText(fresh).Contains("BRAINX_MCP_VERSION"));

            var quoted = Path.Combine(root, "q.toml");
            Check("a path with a quote is refused, not written broken",
                  AgentMcpRegistration.EnsureGrok(quoted, @"C:\O'Brien\brainx-mcp.exe", vault, null) == AgentMcpRegistration.Result.Refused
                  && !File.Exists(quoted));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task McpResolveFallsBackToMirror()
    {
        var root = TempDir("brainx-mcpfallback-");
        try
        {
            var mirror = Path.Combine(root, ".brainx", "mcp", "brainx-mcp.exe");
            var found = Path.Combine(root, "packaged", "brainx-mcp.exe");
            Check("no build and no mirror: null (onboarding says so)", McpRuntimePaths.OrStableMirror(null, mirror) == null);
            Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
            File.WriteAllText(mirror, "");
            Check("no build but a mirror: the mirror", McpRuntimePaths.OrStableMirror(null, mirror) == mirror);
            Check("an empty resolve counts as none", McpRuntimePaths.OrStableMirror("", mirror) == mirror);
            Check("a resolved build always wins over the mirror", McpRuntimePaths.OrStableMirror(found, mirror) == found);
            Check("the default mirror is ~/.brainx/mcp, outside the updater root",
                  McpRuntimePaths.StableExe.EndsWith(Path.Combine(".brainx", "mcp", "brainx-mcp.exe"), StringComparison.OrdinalIgnoreCase)
                  && !McpRuntimePaths.IsRetiredLocation(McpRuntimePaths.StableExe));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        return Task.CompletedTask;
    }

    private static async Task AgentWatcherFiresOnArrival()
    {
        var root = TempDir("brainx-agentwatch-");
        try
        {
            var gemini = Path.Combine(root, ".gemini");
            var grok = Path.Combine(root, ".grok");
            var made = Path.Combine(root, ".made-by-onboarding");
            Directory.CreateDirectory(grok);   // already installed before BrainX started

            var calls = new List<IReadOnlyList<string>>();
            var probes = new Dictionary<string, Func<bool>>
            {
                ["Gemini CLI"] = () => Directory.Exists(gemini),
                ["Grok Build"] = () => Directory.Exists(grok),
                ["Made"] = () => Directory.Exists(made),
                ["Broken"] = () => throw new IOException("probe failed"),
            };
            using var w = new AgentArrivalWatcher(probes, arrived =>
            {
                calls.Add(arrived);
                Directory.CreateDirectory(made);   // onboarding creating a folder of its own
                return Task.CompletedTask;
            }, TimeSpan.FromHours(1));

            Check("the first poll only records the baseline", (await w.PollOnceAsync()).Count == 0 && calls.Count == 0);
            Check("nothing new: nothing fires", (await w.PollOnceAsync()).Count == 0 && calls.Count == 0);

            Directory.CreateDirectory(gemini);
            var a = await w.PollOnceAsync();
            Check("Gemini appearing fires once, by name", a.SequenceEqual(new[] { "Gemini CLI" }) && calls.Count == 1, string.Join(",", a));
            Check("the folder onboarding made is not a second arrival", (await w.PollOnceAsync()).Count == 0 && calls.Count == 1);
            Check("a throwing probe counts as absent, never as an arrival", calls.All(c => !c.Contains("Broken")));

            Directory.Delete(grok);
            Check("an agent going away fires nothing", (await w.PollOnceAsync()).Count == 0 && calls.Count == 1);
            Directory.CreateDirectory(grok);
            var b = await w.PollOnceAsync();
            Check("…and coming back is an arrival again", b.SequenceEqual(new[] { "Grok Build" }) && calls.Count == 2, string.Join(",", b));

            // A callback that throws must not kill the watcher.
            using var w2 = new AgentArrivalWatcher(new Dictionary<string, Func<bool>> { ["X"] = () => Directory.Exists(made) },
                _ => throw new InvalidOperationException("boom"), TimeSpan.FromHours(1));
            Directory.Delete(made);
            await w2.PollOnceAsync();
            Directory.CreateDirectory(made);
            var c = await w2.PollOnceAsync();
            Check("a failing onboarding still reports the arrival and keeps polling",
                  c.SequenceEqual(new[] { "X" }) && (await w2.PollOnceAsync()).Count == 0);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
