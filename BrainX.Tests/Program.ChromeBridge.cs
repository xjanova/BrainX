using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BrainX.Core.Services;
using BrainX.Core.Services.Chrome;
using BrainX.Server.Mcp;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// The built-in Claude in Chrome bridge (2026-10-10) and the owner's privacy
/// rule that comes with it: what a chrome__ tool is asked and what it sees
/// never enters the vault, never syncs, never crosses the remote endpoint —
/// it is kept in a private local store only the owner searches.
///
/// No real claude.exe, browser, extension or network is involved anywhere:
/// discovery runs against fake folder trees with injected probes, and the
/// end-to-end checks run brainx-mcp in a sandbox whose chrome bridge can only
/// start THIS harness as a stub (a sandbox never resolves the real claude.exe).
/// Every temp folder comes from Path.GetTempPath(), so the owner's real
/// %LOCALAPPDATA%\BrainX and vault are never touched.
/// </summary>
internal static partial class Program
{
    private static void RegisterChromeBridgeChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("chrome discovery: the newest VERIFIED desktop build wins, MSIX twin included, deleted folders ignored", ChromeLocatorNewestVerified));
        checks.Add(("chrome discovery: a build without the flag is too old; npm runs node + cli.js, never a shim", ChromeLocatorTooOldAndNpm));
        checks.Add(("chrome child env: parent CLAUDE_CODE_* / ANTHROPIC_* / CLAUDECODE never reach claude.exe", ChromeChildEnvChecks));
        checks.Add(("chrome auth probe: loggedIn / authMethod only; the credentials file is read for a key NAME, never a value", ChromeAuthProbeChecks));
        checks.Add(("chrome extension scan: installed, disabled, Opera roaming, profile names", ChromeExtensionScanChecks));
        checks.Add(("chrome opt-in: the bridge is OFF unless the owner's file says a literal true", ChromeOptInDefaultOff));
        checks.Add(("chrome private store: owner-only folder, DPAPI screenshots, Thai substring search, redaction, retention, clear", ChromePrivateStoreChecks));
        checks.Add(("chrome remote policy: chrome__* and private_browse_search never cross the network", ChromeRemotePolicyChecks));
        checks.Add(("chrome status: every state carries one action and both languages", ChromeStatusShapeChecks));
        checks.Add(("chrome bridge end to end: chrome__ tools, file_upload hidden, images unchanged, NOTHING in the vault, private log searchable", ChromeBridgeEndToEnd));
        checks.Add(("chrome bridge: off by default — no tools, no spawn, a Disabled entry with the way to turn it on", ChromeBridgeOffByDefault));
        checks.Add(("chrome bridge: the remote child refuses private_browse_search and never lists it", ChromeRemoteChildRefuses));
        checks.Add(("chrome bridge: a call during discovery is answered ONCE and discovery survives (slow-start stub race)", ChromeDiscoveryRace));
        checks.Add(("chrome bridge: a server that dies mid-call is reported with the real elapsed time, en + th", ChromePipeClosedEarly));
        checks.Add(("chrome bridge: the private log is written behind the call; long pages get chrome advice", ChromePrivateLogBehindTheCall));
        checks.Add(("chrome bridge: private_browse_search only where an owner can use it; unattended runs refused", ChromePrivateSearchGating));
        checks.Add(("chrome launch args: --settings claude-in-chrome is a working action, never a vault path", ChromeSettingsLaunchArgs));
        checks.Add(("chrome instructions worst case: hooks broken + private browsing + a long vault path — every rule still shown", ChromeInstructionsWorstCase));
        checks.Add(("chrome private log capture: the writer's queue holds only what the store keeps", ChromeCaptureIsBounded));
        RegisterUnattendedRunChecks(checks);
        // Opt-in: reads THIS PC's real Claude Code install and browser profiles.
        // Starts claude.exe only for `--version` and `auth status --json` —
        // never the chrome server, never a browser.
        if (Environment.GetEnvironmentVariable("BRAINX_TEST_LIVE_CLAUDE") == "1")
            checks.Add(("chrome live discovery (opt-in): this PC's Claude Code, sign-in and extension", ChromeLiveDiscovery));
    }

    private static Task ChromeLiveDiscovery()
    {
        var loc = ClaudeCodeLocator.Locate();
        Console.WriteLine($"         locate: {loc.Status} {loc.Install?.Version} {loc.Install?.Path ?? loc.Rejected?.Path}");
        foreach (var c in loc.Candidates) Console.WriteLine($"         candidate: {c.Kind} {c.FolderVersion} {c.Path}");
        Check("a usable Claude Code is found, with the flag as the first argument",
              loc.Status == ClaudeLocateStatus.Found && loc.Install!.McpArgs[loc.Install.FlagIndex] == ChromeBridgeIds.McpFlag);
        if (loc.Install != null)
        {
            var auth = ClaudeAuthProbe.Probe(loc.Install);
            Console.WriteLine($"         auth: {auth.State} via {auth.Source} (method: {auth.Method ?? "-"})");
            Check("the auth probe answers (any state) without throwing", true);
        }
        var scan = ChromeExtensionScanner.Scan();
        foreach (var i in scan.Installs)
            Console.WriteLine($"         extension: {i.BrowserName} / {i.ProfileName} v{i.ExtensionVersion} {(i.Enabled ? "on" : "OFF")}{(scan.RunningBrowsers.Contains(i.Browser) ? " (running)" : "")}");
        Check("the extension scan completes", true);
        return Task.CompletedTask;
    }

    // ───────────── discovery ─────────────

    /// <summary>A fake claude.exe: plain bytes, version in the text, flag optional.</summary>
    private static string FakeClaude(string path, string version, bool withFlag = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"MZ fake claude {version} " + (withFlag ? ChromeBridgeIds.McpFlag : "--no-such-flag") + " " + new string('z', 2000));
        return path;
    }

    /// <summary>The version probe the locator would run, answered from the fake file itself.</summary>
    private static string? FakeVersionProbe(string command, IReadOnlyList<string> prefix)
    {
        var target = prefix.Count > 0 ? prefix[0] : command;   // node + cli.js → the script
        try
        {
            var text = File.ReadAllText(target);
            var m = System.Text.RegularExpressions.Regex.Match(text, @"\d+\.\d+\.\d+");
            return m.Success ? m.Value + " (Claude Code)" : null;
        }
        catch { return null; }
    }

    private static Task ChromeLocatorNewestVerified()
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-chrome-locate-" + Guid.NewGuid().ToString("N"));
        try
        {
            var appData = Path.Combine(root, "Roaming");
            var local = Path.Combine(root, "Local");
            var cc = Path.Combine(appData, "Claude", "claude-code");
            void Verified(string dir) => File.WriteAllText(Path.Combine(dir, ".verified"), new string('a', 64));

            var v289 = FakeClaude(Path.Combine(cc, "2.1.289", "e1f0154146bb", "claude.exe"), "2.1.289");
            Verified(Path.GetDirectoryName(v289)!);
            var v290 = FakeClaude(Path.Combine(cc, "2.1.290", "aaaaaaaaaaaa", "claude.exe"), "2.1.290");
            Verified(Path.GetDirectoryName(v290)!);
            // Newest, but mid-update: no .verified yet.
            var v293 = FakeClaude(Path.Combine(cc, "2.1.293", "83cb0bd7fed4", "claude.exe"), "2.1.293");
            // The version a wrapper script still names, already deleted by an update.
            Directory.CreateDirectory(Path.Combine(cc, "2.1.286"));
            // The MSIX-virtualised twin, newer than 2.1.290.
            var msix = FakeClaude(Path.Combine(local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude",
                                               "claude-code", "2.1.291", "dddddddddddd", "claude.exe"), "2.1.291");
            Verified(Path.GetDirectoryName(msix)!);

            ClaudeCodeLocator.Options Opts() => new()
            {
                AppData = appData, LocalAppData = local, UserProfile = Path.Combine(root, "home"),
                ProgramFiles = Path.Combine(root, "pf"), PathEnv = "",
                VersionProbe = FakeVersionProbe,          // FlagProbe left real: the byte scan itself is under test
            };

            var r1 = ClaudeCodeLocator.Locate(Opts());
            Check("the MSIX twin 2.1.291 beats real-%APPDATA% 2.1.290; unverified 2.1.293 is skipped",
                  r1.Status == ClaudeLocateStatus.Found && r1.Install?.Path == msix && r1.Install.Kind == ClaudeInstallKind.DesktopMsix,
                  $"{r1.Status} {r1.Install?.Path}");
            Check("…the flag is the FIRST argument of an exe launch",
                  r1.Install?.McpArgs.Count == 1 && r1.Install.McpArgs[0] == ChromeBridgeIds.McpFlag && r1.Install.FlagIndex == 0,
                  string.Join(" ", r1.Install?.McpArgs ?? new List<string>()));
            Check("…and the deleted 2.1.286 folder is not a candidate",
                  r1.Candidates.All(c => !c.Path.Contains("2.1.286")), string.Join("; ", r1.Candidates.Select(c => c.Path)));

            Directory.Delete(Path.Combine(local, "Packages"), recursive: true);
            var r2 = ClaudeCodeLocator.Locate(Opts());
            Check("re-resolved after the twin vanished: 2.1.290", r2.Install?.Path == v290, r2.Install?.Path);

            Verified(Path.GetDirectoryName(v293)!);
            var r3 = ClaudeCodeLocator.Locate(Opts());
            Check("once 2.1.293 is verified it wins", r3.Install?.Path == v293 && r3.Install.Version == "2.1.293", r3.Install?.Path);

            // A hash folder with no claude.exe beside a good one (seen on the
            // owner's PC with Codex's content-addressed folders).
            Directory.CreateDirectory(Path.Combine(cc, "2.1.294", "ffffffffffff"));
            Verified(Path.Combine(cc, "2.1.294", "ffffffffffff"));
            var r4 = ClaudeCodeLocator.Locate(Opts());
            Check("a version folder whose hash dir holds only .verified (no exe) is skipped", r4.Install?.Path == v293, r4.Install?.Path);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task ChromeLocatorTooOldAndNpm()
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-chrome-npm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var appData = Path.Combine(root, "Roaming");
            var home = Path.Combine(root, "home");
            var native = FakeClaude(Path.Combine(home, ".local", "bin", "claude.exe"), "2.0.42", withFlag: false);

            ClaudeCodeLocator.Options Opts(string pathEnv) => new()
            {
                AppData = appData, LocalAppData = Path.Combine(root, "Local"), UserProfile = home,
                ProgramFiles = Path.Combine(root, "pf"), PathEnv = pathEnv, VersionProbe = FakeVersionProbe,
            };

            var old = ClaudeCodeLocator.Locate(Opts(""));
            Check("only 2.0.42 (no flag in the binary) → TooOld, never Found",
                  old.Status == ClaudeLocateStatus.TooOld && old.RejectedVersion == "2.0.42" && old.Rejected?.Path == native,
                  $"{old.Status} {old.RejectedVersion}");

            // npm global: the package, its shims, and node on PATH.
            var pkg = Path.Combine(appData, "npm", "node_modules", "@anthropic-ai", "claude-code");
            Directory.CreateDirectory(pkg);
            File.WriteAllText(Path.Combine(pkg, "package.json"),
                """{"name":"@anthropic-ai/claude-code","version":"2.1.50","bin":{"claude":"cli.js"}}""");
            File.WriteAllText(Path.Combine(pkg, "cli.js"), "#!/usr/bin/env node\n// 2.1.50\nconst f='" + ChromeBridgeIds.McpFlag + "';");
            var npmBin = Path.Combine(appData, "npm");
            File.WriteAllText(Path.Combine(npmBin, "claude.cmd"), "@node cli.js %*");
            File.WriteAllText(Path.Combine(npmBin, "claude.ps1"), "node cli.js $args");
            File.WriteAllText(Path.Combine(npmBin, "claude"), "#!/bin/sh\nnode cli.js \"$@\"");
            var nodeDir = Path.Combine(root, "nodejs");
            Directory.CreateDirectory(nodeDir);
            var node = Path.Combine(nodeDir, "node.exe");
            File.WriteAllText(node, "MZ fake node");

            var npm = ClaudeCodeLocator.Locate(Opts(npmBin + Path.PathSeparator + nodeDir));
            Check("npm 2.1.50 is found (and preferred to the too-old native build)",
                  npm.Status == ClaudeLocateStatus.Found && npm.Install?.Kind == ClaudeInstallKind.Npm, $"{npm.Status} {npm.Install?.Path}");
            Check("…started as node.exe + cli.js, with the flag at args[1]",
                  npm.Install?.Command == node && npm.Install.McpArgs.Count == 2
                  && npm.Install.McpArgs[0] == Path.Combine(pkg, "cli.js") && npm.Install.McpArgs[1] == ChromeBridgeIds.McpFlag
                  && npm.Install.FlagIndex == 1,
                  $"{npm.Install?.Command} {string.Join(" ", npm.Install?.McpArgs ?? new List<string>())}");
            Check("…and no candidate anywhere is a .cmd / .ps1 / extensionless shim",
                  npm.Candidates.All(c => c.Command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)),
                  string.Join("; ", npm.Candidates.Select(c => c.Command)));

            // A package.json is data: a bin that climbs out of the package is refused.
            File.WriteAllText(Path.Combine(pkg, "package.json"),
                """{"name":"@anthropic-ai/claude-code","version":"2.1.50","bin":{"claude":"..\\..\\..\\evil.js"}}""");
            File.WriteAllText(Path.Combine(appData, "npm", "evil.js"), ChromeBridgeIds.McpFlag + " 2.9.9");
            var evil = ClaudeCodeLocator.Locate(Opts(npmBin + Path.PathSeparator + nodeDir));
            Check("a package.json bin pointing outside the package is never started",
                  evil.Install?.Path?.EndsWith("evil.js", StringComparison.OrdinalIgnoreCase) != true, evil.Install?.Path);

            Check("the flag scan finds the string in a real file and not in one without it",
                  ClaudeCodeLocator.ContainsFlag(Path.Combine(pkg, "cli.js")) && !ClaudeCodeLocator.ContainsFlag(native));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task ChromeChildEnvChecks()
    {
        var names = new[] { "ANTHROPIC_API_KEY", "ANTHROPIC_BASE_URL", "CLAUDE_CODE_OAUTH_TOKEN", "CLAUDE_CODE_ACCOUNT_UUID", "CLAUDECODE" };
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var n in names) Environment.SetEnvironmentVariable(n, "dummy-" + n);
            Environment.SetEnvironmentVariable("BRAINX_KEEP_ME", "1");
            var psi = ClaudeChildEnv.ProbeStartInfo("x.exe", new[] { "--version" });
            Check("every parent-session variable is stripped",
                  names.All(n => !psi.Environment.ContainsKey(n)), string.Join(",", psi.Environment.Keys.Where(ClaudeChildEnv.IsStripped)));
            Check("…and unrelated ones are kept (PATH, USERPROFILE, APPDATA, a BRAINX_ var)",
                  psi.Environment.ContainsKey("PATH") && psi.Environment.ContainsKey("USERPROFILE")
                  && psi.Environment.ContainsKey("APPDATA") && psi.Environment.ContainsKey("BRAINX_KEEP_ME"));
            Check("the probe has no shell and no window", !psi.UseShellExecute && psi.CreateNoWindow && psi.ArgumentList.Count == 1);
        }
        finally
        {
            foreach (var (n, v) in saved) Environment.SetEnvironmentVariable(n, v);
            Environment.SetEnvironmentVariable("BRAINX_KEEP_ME", null);
        }
        return Task.CompletedTask;
    }

    private static Task ChromeAuthProbeChecks()
    {
        Check("loggedIn:false → NotSignedIn (exit 1 output still parsed)",
              ClaudeAuthProbe.FromJson("""{"loggedIn":false,"authMethod":"none"}""")?.State == ClaudeAuthState.NotSignedIn);
        // The six authMethod values Claude Code 2.1.293's `auth status` really
        // emits (read from the binary): none | claude.ai | oauth_token |
        // api_key | api_key_helper | third_party.
        foreach (var (method, expect) in new[]
                 {
                     ("claude.ai", ClaudeAuthState.SignedIn),
                     ("oauth_token", ClaudeAuthState.SignedIn),
                     ("api_key", ClaudeAuthState.ApiKeyOnly),
                     ("api_key_helper", ClaudeAuthState.ApiKeyOnly),
                     ("third_party", ClaudeAuthState.ApiKeyOnly),
                 })
            Check($"authMethod {method} → {expect}",
                  ClaudeAuthProbe.FromJson($$"""{"loggedIn":true,"authMethod":"{{method}}","email":"x@y","orgName":"o"}""")?.State == expect);
        Check("authMethod none (loggedIn:true is not trusted over it) → NotSignedIn",
              ClaudeAuthProbe.Classify("none") == ClaudeAuthState.NotSignedIn);
        Check("(control) a value Claude Code does not emit stays Unknown — not a guess either way",
              ClaudeAuthProbe.Classify("some_future_method") == ClaudeAuthState.Unknown);
        Check("a banner before the JSON is tolerated",
              ClaudeAuthProbe.FromJson("warning: something\n{\"loggedIn\":true,\"authMethod\":\"oauth_token\"}")?.State == ClaudeAuthState.SignedIn);

        // third_party used to fall to Unknown → Ready → "Connect", which loops
        // on NotReachable for someone who can never drive Chrome.
        var tp = ChromeBridgeStatus.Build(new ChromeBridgeStatus.Inputs
        {
            Enabled = true,
            Locate = new ClaudeLocateResult { Status = ClaudeLocateStatus.Found,
                Install = new ClaudeCodeInstall(@"C:\x\claude.exe", ClaudeInstallKind.Desktop, @"C:\x\claude.exe", Array.Empty<string>(), "2.1.293") },
            Auth = ClaudeAuthProbe.FromJson("""{"loggedIn":true,"authMethod":"third_party"}"""),
            Scan = new ChromeScanResult(),
        });
        Check("third_party (Bedrock / Vertex / Foundry / gateway) → ApiKeyOnly, says so in en + th, offers the claude.ai sign-in",
              tp["state"]?.ToString() == "ApiKeyOnly" && tp["message_en"]!.ToString().Contains("cloud provider")
              && tp["message_th"]!.ToString().Contains("Bedrock") && tp["action"]?["kind"]?.ToString() == "run_console",
              tp.ToString(Newtonsoft.Json.Formatting.None));
        Check("garbage → no answer (falls back), never a throw", ClaudeAuthProbe.FromJson("not json {") == null);

        var root = Path.Combine(Path.GetTempPath(), "brainx-chrome-cred-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string secret = "sk-ant-oat01-DUMMY-TOKEN-VALUE-NEVER-READ";
            var withKey = Path.Combine(root, "a.json");
            File.WriteAllText(withKey, "{\"mcpOAuth\":{\"x\":{\"accessToken\":\"" + secret + "\"}},\"claudeAiOauth\":{\"accessToken\":\"" + secret + "\",\"refreshToken\":\"" + secret + "\"}}");
            var r = ClaudeAuthProbe.FromCredentialsFile(withKey);
            Check("credentials with a claudeAiOauth entry → SignedIn by PRESENCE", r.State == ClaudeAuthState.SignedIn && r.Source == "credentials-file");
            Check("…and the token value appears nowhere in what comes back", !r.ToString().Contains(secret));

            var mcpOnly = Path.Combine(root, "b.json");
            File.WriteAllText(mcpOnly, "{\"mcpOAuth\":{\"x\":{\"claudeAiOauth\":\"nested-not-top-level\"}}}");
            Check("only MCP OAuth entries (as on the owner's PC) → NotSignedIn; a NESTED key does not count",
                  ClaudeAuthProbe.FromCredentialsFile(mcpOnly).State == ClaudeAuthState.NotSignedIn);
            Check("a missing file → Unknown, never a throw",
                  ClaudeAuthProbe.FromCredentialsFile(Path.Combine(root, "none.json")).State == ClaudeAuthState.Unknown);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
        return Task.CompletedTask;
    }

    // ───────────── extension scan ─────────────

    private static void FakeExtension(string profile, string version)
    {
        var dir = Path.Combine(profile, "Extensions", ChromeBridgeIds.ExtensionId, version + "_0");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{\"name\":\"Claude\",\"version\":\"" + version + "\"}");
    }

    private static Task ChromeExtensionScanChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-chrome-scan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var local = Path.Combine(root, "Local");
            var roaming = Path.Combine(root, "Roaming");
            var chrome = Path.Combine(local, "Google", "Chrome", "User Data");
            FakeExtension(Path.Combine(chrome, "Default"), "1.0.99");
            FakeExtension(Path.Combine(chrome, "Default"), "1.0.100");
            Directory.CreateDirectory(Path.Combine(chrome, "Profile 2"));   // no extension
            File.WriteAllText(Path.Combine(chrome, "Local State"),
                """{"profile":{"info_cache":{"Default":{"name":"งานหลัก"},"Profile 2":{"name":"Other"}}}}""");
            File.WriteAllText(Path.Combine(chrome, "Default", "Secure Preferences"),
                "{\"extensions\":{\"settings\":{\"" + ChromeBridgeIds.ExtensionId + "\":{\"disable_reasons\":[],\"from_webstore\":true}}}}");

            var edge = Path.Combine(local, "Microsoft", "Edge", "User Data");
            FakeExtension(Path.Combine(edge, "Profile 1"), "1.0.100");
            File.WriteAllText(Path.Combine(edge, "Profile 1", "Secure Preferences"),
                "{\"extensions\":{\"settings\":{\"" + ChromeBridgeIds.ExtensionId + "\":{\"disable_reasons\":[1]}}}}");

            FakeExtension(Path.Combine(roaming, "Opera Software", "Opera Stable"), "1.0.98");

            var scan = ChromeExtensionScanner.Scan(new ChromeExtensionScanner.Options
            {
                LocalAppData = local, AppData = roaming, IsRunning = p => p == "chrome",
            });
            var c = scan.Installs.FirstOrDefault(i => i.Browser == "chrome");
            Check("Chrome Default found with the HIGHEST version and its Thai profile name",
                  c != null && c.ExtensionVersion == "1.0.100" && c.ProfileName == "งานหลัก" && c.Enabled,
                  c?.ToString());
            Check("a profile without the extension is not reported", scan.Installs.Count(i => i.Browser == "chrome") == 1);
            var e = scan.Installs.FirstOrDefault(i => i.Browser == "edge");
            Check("Edge 'Profile 1' found, DISABLED via Secure Preferences disable_reasons", e != null && !e.Enabled, e?.ToString());
            var o = scan.Installs.FirstOrDefault(i => i.Browser == "opera");
            Check("Opera found under Roaming", o != null && o.ExtensionVersion == "1.0.98", o?.ToString());
            Check("running browsers are reported", scan.RunningBrowsers.SetEquals(new[] { "chrome" }) && scan.AnyEnabledRunning);

            var none = ChromeExtensionScanner.Scan(new ChromeExtensionScanner.Options
            {
                LocalAppData = Path.Combine(root, "nothing"), AppData = Path.Combine(root, "nothing2"), IsRunning = _ => false,
            });
            Check("no browsers at all → empty, never a throw", !none.AnyInstalled);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
        return Task.CompletedTask;
    }

    // ───────────── settings ─────────────

    private static Task ChromeOptInDefaultOff()
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-chrome-optin-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var s0 = ChromeBridgeSettings.Load(root);
            Check("no file → OFF, 30 days", !s0.Enabled && s0.RetentionDays == 30);

            File.WriteAllText(ChromeBridgeSettings.PathIn(root), "{\"enabled\":\"true\",\"retentionDays\":\"999999\"}");
            var s1 = ChromeBridgeSettings.Load(root);
            Check("\"true\" as a STRING is not on; a string retention is ignored", !s1.Enabled && s1.RetentionDays == 30);

            File.WriteAllText(ChromeBridgeSettings.PathIn(root), "{ broken json");
            Check("a broken file → OFF", !ChromeBridgeSettings.Load(root).Enabled);

            new ChromeBridgeSettings { Enabled = true, RetentionDays = 7 }.Save(root);
            var s2 = ChromeBridgeSettings.Load(root);
            Check("saved ON / 7 days round-trips", s2.Enabled && s2.RetentionDays == 7);

            new ChromeBridgeSettings { Enabled = true, RetentionDays = 100000 }.Save(root);
            Check("retention is clamped to 10 years", ChromeBridgeSettings.Load(root).RetentionDays == ChromeBridgeSettings.MaxRetentionDays);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
        return Task.CompletedTask;
    }

    // ───────────── private store ─────────────

    private static Task ChromePrivateStoreChecks()
    {
        var root = Path.Combine(Path.GetTempPath(), "brainx-chrome-store-" + Guid.NewGuid().ToString("N"), "private");
        try
        {
            var store = new PrivateBrowseStore(root);
            var m = "M" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var shot = ChromeStubServer.ScreenshotBytes(m);
            var rec = new PrivateBrowseRecord
            {
                Agent = "claude", Tool = "get_page_text", Url = ChromeStubServer.MarkerUrl(m),
                ArgsJson = "{\"tabId\":101}", ResultText = ChromeStubServer.PageText(m),
            };
            rec.Images.Add(("image/png", shot));
            var id = store.Record(rec);

            Check("the private folder is protected and granted to this Windows user alone", store.IsOwnerOnly());
            var plainDir = Path.Combine(Path.GetDirectoryName(root)!, "plain");
            Directory.CreateDirectory(plainDir);
            Check("(control) a folder made the ordinary way beside it is NOT owner-only (it inherits the parent's ACL)",
                  !new PrivateBrowseStore(plainDir).IsOwnerOnly());
            var hits = store.Search("ยอดเงินคงเหลือ");
            Check("a Thai phrase from the middle of the page is found (trigram, no word breaks needed)",
                  hits.Count == 1 && hits[0].Id == id && hits[0].Tool == "get_page_text" && hits[0].Url == ChromeStubServer.MarkerUrl(m),
                  hits.Count + " hit(s)");
            Check("…with a snippet around the match", hits.FirstOrDefault()?.Snippet.Contains("ยอดเงินคงเหลือ") == true, hits.FirstOrDefault()?.Snippet);
            Check("the URL is searchable", store.Search($"private-{m.ToLowerInvariant()}").Count == 1);
            Check("every term must match (AND)", store.Search($"PAGE-TEXT-{m} nosuchwordanywhere").Count == 0);
            Check("a 2-character term still works (LIKE fallback)", store.Search("12").Count == 1);
            Check("the credential on the page was masked before it was stored", store.Search(ChromeStubServer.SecretValue(m)).Count == 0
                  && store.Search("SECRETSECRET").Count == 0);
            Check("FTS / SQL syntax in a query is inert", store.Search("\") OR 1=1 --").Count == 0 && store.Search("* NEAR(").Count == 0);

            var shotId = hits.FirstOrDefault()?.Shots.FirstOrDefault()?.Id;
            var file = shotId == null ? null : Path.Combine(store.ShotsDir, shotId + ".bin");
            Check("the screenshot is on disk", file != null && File.Exists(file));
            var onDisk = file != null && File.Exists(file) ? File.ReadAllBytes(file) : Array.Empty<byte>();
            Check("…DPAPI-wrapped: not the PNG, and the marker is not in the file",
                  onDisk.Length > 0 && !onDisk.Take(8).SequenceEqual(shot.Take(8))
                  && !Encoding.ASCII.GetString(onDisk).Contains(m));
            var back = store.ReadShot(shotId, out var mime);
            Check("…and reads back byte-exact for this user", back != null && back.SequenceEqual(shot) && mime == "image/png");
            Check("a shot id that is not one (path traversal) is refused",
                  store.ReadShot("..\\..\\browse", out _) == null && store.ReadShot(new string('0', 32), out _) == null);

            // Retention: an entry from 40 days ago goes at 30 days — and is
            // never SHOWN while it waits for a prune (reads apply the window).
            store.Record(new PrivateBrowseRecord { TsUtc = DateTime.UtcNow.AddDays(-40), Tool = "navigate", Url = "https://old.example/", ResultText = "OLD-ENTRY" });
            Check("(control) the 40-day-old row IS in the table before pruning", RowCount(store, "OLD-ENTRY") == 1);
            Check("…but a 30-day log never shows it: not in Search, not in Stats",
                  store.Search("OLD-ENTRY").Count == 0 && store.Search("").All(h => h.Url != "https://old.example/") && store.Stats().Entries == 1,
                  $"stats {store.Stats().Entries}");
            var removed = store.Prune(30);
            Check("pruning at 30 days removes the 40-day-old entry and keeps today's",
                  removed == 1 && RowCount(store, "OLD-ENTRY") == 0 && store.Search("ยอดเงิน").Count == 1, $"removed {removed}");

            // The review's case: the owner stops browsing. Nothing records,
            // nothing prunes (throttled), the entry simply ages past 30 days.
            var agedShot = hits.FirstOrDefault()?.Shots.FirstOrDefault()?.Id;
            Check("(control) today's entry, its text and its screenshot are all readable",
                  store.Search("ยอดเงินคงเหลือ").Count == 1 && store.ReadEntry(id) != null && store.ReadShot(agedShot, out _) != null);
            AgeEntry(store, id, days: 45);
            Check("aged to 45 days with no new Record: Search, Stats, ReadEntry and ReadShot all stop returning it",
                  store.Search("ยอดเงินคงเหลือ").Count == 0 && store.Search("").Count == 0 && store.Stats().Entries == 0
                  && store.ReadEntry(id) == null && store.ReadShot(agedShot, out _) == null,
                  $"search {store.Search("ยอดเงินคงเหลือ").Count}, stats {store.Stats().Entries}");
            var longer = new PrivateBrowseStore(root) { RetentionOverride = 90 };
            Check("(control) the same row under a 90-day window is still there — the window, not a delete, hid it",
                  longer.Search("ยอดเงินคงเหลือ").Count == 1 && RowCount(store, "PAGE-TEXT-" + m) == 1);
            AgeEntry(store, id, days: 0);

            var cleared = store.Clear();
            Check("Clear removes every entry and screenshot", cleared == 1 && store.Search("").Count == 0
                  && (!Directory.Exists(store.ShotsDir) || Directory.GetFiles(store.ShotsDir).Length == 0), $"cleared {cleared}");
            var db = File.ReadAllBytes(store.DbPath);
            Check("…and the cleared text is not left in the database file (secure_delete + VACUUM)",
                  !Encoding.UTF8.GetString(db).Contains("PAGE-TEXT-" + m));

            // URL redaction: a callback URL carries its token in the fragment.
            var jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ" + m + "In0.c2lnbmF0dXJlLXNpZ25hdHVyZS1zaWc";
            var tokenUrl = $"https://app-{m.ToLowerInvariant()}.example/callback#access_token={jwt}&state=xyz";
            store.Record(new PrivateBrowseRecord { Tool = "navigate", Url = tokenUrl, ArgsJson = new JObject { ["url"] = tokenUrl }.ToString() });
            var urlHit = store.Search($"app-{m.ToLowerInvariant()}");
            Check("(control) the callback URL itself is stored and searchable",
                  urlHit.Count == 1 && urlHit[0].Url?.Contains("/callback") == true, urlHit.FirstOrDefault()?.Url);
            Check("…with its token masked in the url column the window and the search show",
                  urlHit.Count == 1 && urlHit[0].Url?.Contains(jwt) == false && urlHit[0].Url?.Contains("access_token=") == true, urlHit.FirstOrDefault()?.Url);
            Check("…and the token is nowhere in browse.db or its WAL (url column, args, FTS index)",
                  !DbFilesContain(store, jwt[..40]) && !DbFilesContain(store, "c2lnbmF0dXJlLXNpZ25hdHVyZS1zaWc"));

            // Review rev2: an OPAQUE token in the fragment (an OAuth implicit
            // grant). No token shape knows ya29.…, so only the key=value rule
            // can mask it — and '#' was not one of that rule's delimiters, so
            // it was kept in the url column, the index and the args.
            var opaque = "ya29.A0ARrdaM" + m + "x9Qz7Lk2Wm4Np6Rt8Vb0Cd3Fg5Hj";
            Check("(control) an opaque ya29 token has no shape the redactor knows by itself — only its key can give it away",
                  SecretRedactor.Redact(opaque) == opaque, SecretRedactor.Redact(opaque));
            var implicitUrl = $"https://oauth-{m.ToLowerInvariant()}.example/oauthcb#access_token={opaque}&token_type=Bearer&expires_in=3599";
            store.Record(new PrivateBrowseRecord { Tool = "navigate", Url = implicitUrl, ArgsJson = new JObject { ["url"] = implicitUrl }.ToString() });
            var implicitHit = store.Search($"oauth-{m.ToLowerInvariant()}");
            Check("(control) the implicit-grant callback is stored and searchable by its host",
                  implicitHit.Count == 1 && implicitHit[0].Url?.Contains("/oauthcb#access_token=") == true, implicitHit.FirstOrDefault()?.Url);
            Check("an opaque #access_token=ya29… is masked in the url column, and is nowhere in browse.db or its WAL (args, index)",
                  implicitHit.Count == 1 && implicitHit[0].Url?.Contains(opaque) == false && !DbFilesContain(store, opaque[..24]),
                  implicitHit.FirstOrDefault()?.Url);
            Check("…a token in a path segment (/session_token=…) is masked too",
                  !SecretRedactor.Redact($"https://app.example/cb/session_token={opaque}").Contains(opaque));
            Check("(control) a plain fragment and a plain path are left alone",
                  SecretRedactor.Redact("https://example.com/docs/setup#install") == "https://example.com/docs/setup#install"
                  && SecretRedactor.Redact("tail -n 200 /var/log/nginx/error.log") == "tail -n 200 /var/log/nginx/error.log");
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); } catch { } }

        // ── the text database has a size cap ──
        var capRoot = Path.Combine(Path.GetTempPath(), "brainx-chrome-cap-" + Guid.NewGuid().ToString("N"), "private");
        try
        {
            const long cap = 256 * 1024;
            var capped = new PrivateBrowseStore(capRoot) { MaxDbBytes = cap };
            var rnd = new Random(7);
            string Page(int i)
            {
                var sb = new StringBuilder($"CAPPAGE-{i:D3} ");
                while (sb.Length < 20 * 1024) sb.Append((char)('a' + rnd.Next(26))).Append((char)('a' + rnd.Next(26))).Append(rnd.Next(1000)).Append(' ');
                return sb.ToString();
            }
            for (var i = 0; i < 24; i++)
                capped.Record(new PrivateBrowseRecord { TsUtc = DateTime.UtcNow.AddMinutes(-100 + i), Tool = "get_page_text", ResultText = Page(i) });
            var before = capped.UsedDbBytes();
            Check("(control) 24 pages of 20 KB put the database over a 256 KB cap", before > cap, $"{before / 1024} KB in use");
            var trimmed = capped.Prune(30);
            var after = capped.UsedDbBytes();
            Check("Prune brings it under the cap, oldest first, newest kept",
                  after <= cap && trimmed > 0 && capped.Search("CAPPAGE-023").Count == 1 && capped.Search("CAPPAGE-000").Count == 0,
                  $"{before / 1024} KB → {after / 1024} KB, removed {trimmed}");
            Check("Stats reports the database's size on disk for the settings card", capped.Stats().DbBytes > 0);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(capRoot)!, recursive: true); } catch { } }
        return Task.CompletedTask;
    }

    /// <summary>Rows whose text holds <paramref name="needle"/>, ignoring retention — straight SQL.</summary>
    private static long RowCount(PrivateBrowseStore store, string needle)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM entries WHERE instr(COALESCE(result,'') || COALESCE(url,'') || COALESCE(args,''), $n) > 0;";
        cmd.Parameters.AddWithValue("$n", needle);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>Move an entry's timestamp <paramref name="days"/> into the past — what time does to a log nobody writes to.</summary>
    private static void AgeEntry(PrivateBrowseStore store, long id, int days)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE entries SET ts_ticks = $t WHERE id = $id; UPDATE shots SET ts_ticks = $t WHERE entry_id = $id;";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.AddDays(-days).Ticks);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static bool DbFilesContain(PrivateBrowseStore store, string needle)
    {
        foreach (var p in new[] { store.DbPath, store.DbPath + "-wal" })
        {
            if (!File.Exists(p)) continue;
            byte[] bytes;
            using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream()) { fs.CopyTo(ms); bytes = ms.ToArray(); }
            if (Encoding.UTF8.GetString(bytes).Contains(needle, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    // ───────────── remote policy + status shape ─────────────

    private static Task ChromeRemotePolicyChecks()
    {
        foreach (var t in new[] { "private_browse_search", "chrome__navigate", "chrome__get_page_text", "chrome_navigate", "CHROME__computer" })
            Check($"'{t}' is refused at every scope, as hard-blocked",
                  !McpRemotePolicy.IsAllowed(t, McpScope.ReadWrite) && !McpRemotePolicy.IsAllowed(t, McpScope.Read)
                  && McpRemotePolicy.IsHardBlocked(t) && McpRemotePolicy.DenyReason(t, McpScope.ReadWrite).Contains("permanently"));
        var list = JObject.Parse("""{"result":{"tools":[{"name":"brain_search"},{"name":"chrome__navigate"},{"name":"private_browse_search"}]}}""");
        var dropped = McpRemotePolicy.FilterToolsList(list, McpScope.ReadWrite);
        Check("tools/list over HTTP keeps brain_search and drops both",
              dropped.Contains("chrome__navigate") && dropped.Contains("private_browse_search")
              && (list["result"]!["tools"] as JArray)!.Count == 1);
        Check("brain_search is unaffected", McpRemotePolicy.IsAllowed("brain_search", McpScope.Read));

        var vault = Path.Combine(Path.GetTempPath(), "brainx-chrome-reserved-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
            File.WriteAllText(McpBridgeConfig.PathFor(vault),
                """{"version":1,"bridges":[{"id":"chrome","enabled":true,"command":"evil.exe","private":false,"builtIn":true},{"id":"unity","enabled":false,"command":"uv"}]}""");
            var defs = McpBridgeConfig.Load(vault, _ => { });
            Check("a config entry named 'chrome' is refused (the id is reserved for the built-in bridge)",
                  defs.All(d => d.Id != "chrome") && defs.Any(d => d.Id == "unity"));
            Check("…and the file can never mark an entry Private or BuiltIn", defs.All(d => !d.Private && !d.BuiltIn && !d.StripClaudeEnv));
        }
        finally { try { Directory.Delete(vault, recursive: true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task ChromeStatusShapeChecks()
    {
        var found = new ClaudeLocateResult
        {
            Status = ClaudeLocateStatus.Found,
            Install = new ClaudeCodeInstall(@"C:\x\claude.exe", ClaudeInstallKind.Desktop, @"C:\x\claude.exe", Array.Empty<string>(), "2.1.293"),
        };
        var scanOk = new ChromeScanResult();
        scanOk.Installs.Add(new ChromeExtensionInstall("chrome", "Google Chrome", "chrome", "Default", "Default", "1.0.100", true));
        scanOk.RunningBrowsers.Add("chrome");
        var signedIn = new ClaudeAuthResult(ClaudeAuthState.SignedIn, "claude.ai", "cli");

        ChromeBridgeStatus.Inputs In(bool enabled = true, bool remote = false, ClaudeLocateResult? loc = null,
                                     ClaudeAuthResult? auth = null, ChromeScanResult? scan = null, ChromeRuntimeSnapshot? rt = null) => new()
        {
            Enabled = enabled, Remote = remote, Locate = loc ?? found, Auth = auth ?? signedIn, Scan = scan ?? scanOk,
            Runtime = rt ?? new ChromeRuntimeSnapshot(),
        };

        var cases = new (string Expect, ChromeBridgeStatus.Inputs Inputs)[]
        {
            ("Disabled", In(enabled: false)),
            ("Disabled", In(remote: true)),
            ("NoClaudeCode", In(loc: new ClaudeLocateResult { Status = ClaudeLocateStatus.NotFound })),
            ("ClaudeTooOld", In(loc: new ClaudeLocateResult { Status = ClaudeLocateStatus.TooOld, RejectedVersion = "2.0.42",
                Rejected = new ClaudeCodeCandidate(@"C:\u\.local\bin\claude.exe", ClaudeInstallKind.Native, @"C:\u\.local\bin\claude.exe", Array.Empty<string>(), null) })),
            ("NotSignedIn", In(auth: new ClaudeAuthResult(ClaudeAuthState.NotSignedIn, "none", "cli"))),
            ("ApiKeyOnly", In(auth: new ClaudeAuthResult(ClaudeAuthState.ApiKeyOnly, "api_key", "cli"))),
            ("ExtensionMissing", In(scan: new ChromeScanResult())),
            ("ExtensionDisabled", In(scan: Disabled())),
            ("BrowserClosed", In(scan: Closed())),
            ("Ready", In()),
            ("Connecting", In(rt: new ChromeRuntimeSnapshot { Connecting = true })),
            ("Connected", In(rt: new ChromeRuntimeSnapshot { Live = true, LastOutcome = "ok", Tools = 21 })),
            ("Connected", In(scan: new ChromeScanResult(), rt: new ChromeRuntimeSnapshot { Live = true, LastOutcome = "ok" })),
            ("NotReachable", In(rt: new ChromeRuntimeSnapshot { LastOutcome = "not-reachable" })),
            ("MultiBrowser", In(rt: new ChromeRuntimeSnapshot { Live = true, LastOutcome = "multi-browser" })),
            ("Failed", In(rt: new ChromeRuntimeSnapshot { LastOutcome = "failed", FailureDetail = "Claude in Chrome is disabled by your organization policy." })),
        };
        foreach (var (expect, inputs) in cases)
        {
            var o = ChromeBridgeStatus.Build(inputs);
            var act = o["action"] as JObject;
            var oneAction = act != null && act["kind"] != null
                            && (act["kind"]!.ToString() == "none" || (!string.IsNullOrEmpty(act["label_en"]?.ToString()) && !string.IsNullOrEmpty(act["label_th"]?.ToString())));
            Check($"{expect}: state, en + th message, one action",
                  o["state"]?.ToString() == expect && !string.IsNullOrWhiteSpace(o["message_en"]?.ToString())
                  && !string.IsNullOrWhiteSpace(o["message_th"]?.ToString()) && oneAction && o["browsers"] is JArray
                  && o.ContainsKey("claudePath") && o.ContainsKey("claudeVersion"),
                  o.ToString(Newtonsoft.Json.Formatting.None));
        }

        var api = ChromeBridgeStatus.Build(In(auth: new ClaudeAuthResult(ClaudeAuthState.ApiKeyOnly, "api_key", "cli")));
        Check("ApiKeyOnly says plainly that an API key cannot drive Chrome (en + th), and offers 'auth login --claudeai'",
              api["message_en"]!.ToString().Contains("API key or API credit cannot drive Chrome")
              && api["message_th"]!.ToString().Contains("API key")
              && api["action"]!["kind"]!.ToString() == "run_console"
              && api["action"]!["target"]!.ToString().Contains("auth login --claudeai"));

        // An unattended broker / Mind run: off whatever else is true, and no
        // "Turn on in BrainX" button — the switch is already on.
        var un = ChromeBridgeStatus.Build(new ChromeBridgeStatus.Inputs
        {
            Enabled = true, Unattended = true, Locate = found, Auth = signedIn, Scan = scanOk,
            Runtime = new ChromeRuntimeSnapshot { Live = true, LastOutcome = "ok", Tools = 21 },
        });
        Check("unattended run: Disabled over everything else, says why in en + th, action none, enabled:false",
              un["state"]?.ToString() == "Disabled" && un["action"]?["kind"]?.ToString() == "none"
              && un["message_en"]!.ToString().Contains("unattended runs") && un["message_th"]!.ToString().Contains("ไม่มีคนดู")
              && un["enabled"]?.Value<bool>() == false && un["unattended"]?.Value<bool>() == true,
              un.ToString(Newtonsoft.Json.Formatting.None));
        Check("(control) the same inputs attended: Connected",
              ChromeBridgeStatus.Build(new ChromeBridgeStatus.Inputs
              {
                  Enabled = true, Locate = found, Auth = signedIn, Scan = scanOk,
                  Runtime = new ChromeRuntimeSnapshot { Live = true, LastOutcome = "ok", Tools = 21 },
              })["state"]?.ToString() == "Connected");
        return Task.CompletedTask;

        ChromeScanResult Disabled()
        {
            var s = new ChromeScanResult();
            s.Installs.Add(new ChromeExtensionInstall("edge", "Microsoft Edge", "msedge", "Default", "Default", "1.0.100", false));
            s.RunningBrowsers.Add("edge");
            return s;
        }
        ChromeScanResult Closed()
        {
            var s = new ChromeScanResult();
            s.Installs.Add(new ChromeExtensionInstall("chrome", "Google Chrome", "chrome", "Default", "Default", "1.0.100", true));
            return s;
        }
    }

    // ───────────── end to end ─────────────

    private sealed class ChromeSession : IAsyncDisposable
    {
        public required Process Server { get; init; }
        /// <summary>The initialize answer (server instructions included).</summary>
        public JObject? Init { get; init; }
        public string Instructions => Init?["result"]?["instructions"]?.ToString() ?? "";
        private int _id = 10;
        public Task<JObject?> Call(string tool, JObject? args = null) =>
            Rpc(Server, Interlocked.Increment(ref _id), "tools/call", new JObject { ["name"] = tool, ["arguments"] = args ?? new JObject() });
        public Task<JObject?> List() => Rpc(Server, Interlocked.Increment(ref _id), "tools/list", new JObject());
        public async Task<JObject?> Status()
        {
            var r = await Call("bridge_status");
            var text = r?["result"]?["content"]?[0]?["text"]?.ToString();
            try { return text == null ? null : JObject.Parse(text)["chrome"] as JObject; } catch { return null; }
        }
        public ValueTask DisposeAsync()
        {
            // Close stdin like a real client: the brain then closes its chrome
            // child the same way, which is what is being exercised.
            try { Server.StandardInput.Close(); } catch { }
            try { if (!Server.WaitForExit(8000)) Server.Kill(entireProcessTree: true); } catch { }
            Server.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<ChromeSession> StartChromeSession(string exe, string vault, string privateDir,
        Dictionary<string, string?> extraEnv, bool bridges = true)
    {
        var psi = new ProcessStartInfo(exe, "--serve")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, StandardOutputEncoding = new UTF8Encoding(false), StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.Environment["BRAINX_VAULT"] = vault;
        psi.Environment["BRAINX_MCP_LAUNCHER_CHILD"] = "1";
        psi.Environment["BRAINX_SANDBOX"] = "1";
        psi.Environment["BRAINX_CANARY"] = "0";
        psi.Environment["BRAINX_PRIVATE_DIR"] = privateDir;
        psi.Environment["BRAINX_BUS_SEAL_KEY"] = Path.Combine(Path.GetDirectoryName(vault)!, "bus-seal.key");
        psi.Environment.Remove(StubMcpServer.EnvFlag);
        if (bridges) psi.Environment["BRAINX_SANDBOX_BRIDGES"] = "1";
        foreach (var (k, v) in extraEnv)
            if (v == null) psi.Environment.Remove(k); else psi.Environment[k] = v;

        var server = Process.Start(psi)!;
        server.StandardInput.AutoFlush = true;
        _ = Task.Run(async () => { try { while (await server.StandardError.ReadLineAsync() != null) { } } catch { } });
        var init = await Rpc(server, 1, "initialize", new JObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JObject(),
            ["clientInfo"] = new JObject { ["name"] = "cluadex", ["version"] = "1" },
        });
        await server.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        return new ChromeSession { Server = server, Init = init };
    }

    /// <summary>A sandboxed brainx-mcp with the chrome bridge ON and the stub
    /// as its claude.exe — the setup most of the chrome checks share.</summary>
    private static (string Root, string Vault, string PrivateDir) ChromeSandboxDirs(string prefix, bool enabled = true)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var privateDir = Path.Combine(root, "localappdata", "BrainX", "private");
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        BusSeal.EnsureKey(Path.Combine(root, "bus-seal.key"));
        if (enabled) new ChromeBridgeSettings { Enabled = true, RetentionDays = 30 }.Save(privateDir);
        return (root, vault, privateDir);
    }

    private static Dictionary<string, string?> StubEnv(string marker, string root, Dictionary<string, string?>? more = null)
    {
        var env = new Dictionary<string, string?>
        {
            ["BRAINX_CHROME_STUB_COMMAND"] = Environment.ProcessPath,
            [ChromeStubServer.EnvFlag] = "1",
            [ChromeStubServer.MarkerEnv] = marker,
            [ChromeStubServer.StartedEnv] = Path.Combine(root, "stub-started.txt"),
            ["BRAINX_CHROME_STUB_RUNNING"] = "1",
        };
        foreach (var (k, v) in more ?? new()) env[k] = v;
        return env;
    }

    /// <summary>The stub must prove it is the stub before a result is trusted.</summary>
    private static async Task<bool> StubProven(ChromeSession s)
    {
        var st = await s.Status();
        var ok = st != null && string.Equals(st["claudePath"]?.ToString(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        Check("(stub) bridge_status names the harness stub as claude.exe — never a real one", ok, st?.ToString(Newtonsoft.Json.Formatting.None));
        return ok;
    }

    /// <summary>relative path → sha256 of every file under the vault.</summary>
    private static Dictionary<string, string> SnapshotTree(string dir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir)) return map;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { map[Path.GetRelativePath(dir, f)] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))); }
            catch { map[Path.GetRelativePath(dir, f)] = "unreadable"; }
        }
        return map;
    }

    private static List<string> FilesContaining(string dir, IEnumerable<string> needles)
    {
        var hits = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            string text;
            try { text = Encoding.UTF8.GetString(File.ReadAllBytes(f)); } catch { continue; }
            foreach (var n in needles)
                if (text.Contains(n, StringComparison.OrdinalIgnoreCase)) { hits.Add($"{Path.GetRelativePath(dir, f)} ∋ {n}"); }
        }
        return hits;
    }

    private static string ResultText(JObject? r) =>
        string.Concat((r?["result"]?["content"] as JArray ?? new JArray()).OfType<JObject>()
            .Where(b => b["type"]?.ToString() == "text").Select(b => b["text"]?.ToString()));

    private static bool IsError(JObject? r) => r?["result"]?["isError"]?.Type == JTokenType.Boolean && r["result"]!["isError"]!.Value<bool>();

    private static async Task ChromeBridgeEndToEnd()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-chrome-e2e-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var privateDir = Path.Combine(root, "localappdata", "BrainX", "private");
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        BusSeal.EnsureKey(Path.Combine(root, "bus-seal.key"));
        new ChromeBridgeSettings { Enabled = true, RetentionDays = 30 }.Save(privateDir);

        var m = "M" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var started = Path.Combine(root, "stub-started.txt");
        var env = new Dictionary<string, string?>
        {
            ["BRAINX_CHROME_STUB_COMMAND"] = Environment.ProcessPath,
            [ChromeStubServer.EnvFlag] = "1",
            [ChromeStubServer.MarkerEnv] = m,
            [ChromeStubServer.StartedEnv] = started,
            ["BRAINX_CHROME_STUB_RUNNING"] = "1",
            // The parent-session variables a Claude host puts in brainx-mcp's
            // environment. None may reach the chrome child.
            ["ANTHROPIC_API_KEY"] = "dummy-not-for-the-child",
            ["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:1",
            ["CLAUDE_CODE_OAUTH_TOKEN"] = "dummy-not-for-the-child",
            ["CLAUDE_CODE_ENTRYPOINT"] = "desktop",
        };

        ChromeSession? s = null;
        try
        {
            s = await StartChromeSession(exe, vault, privateDir, env);

            // ── the stub proves itself before anything is trusted ──
            var st0 = await s.Status();
            Check("bridge_status has a chrome entry whose claudePath is the harness stub — never a real claude.exe",
                  st0 != null && string.Equals(st0["claudePath"]?.ToString(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase),
                  st0?.ToString(Newtonsoft.Json.Formatting.None));
            if (st0 == null || !string.Equals(st0["claudePath"]?.ToString(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;

            var list = await s.List();
            var tools = (list?["result"]?["tools"] as JArray ?? new JArray()).OfType<JObject>().ToList();
            var names = tools.Select(t => t["name"]!.ToString()).ToList();
            var fixture = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "p293_tools.json")))["result"]!["tools"] as JArray;
            var expected = fixture!.OfType<JObject>().Select(t => t["name"]!.ToString()).Where(n => n != "file_upload").Select(n => "chrome__" + n).ToList();

            var st1 = await s.Status();
            Check("the bridge answered as the STUB (serverInfo marker) — the real server would say 'Claude in Chrome'",
                  st1?["server"]?.ToString().StartsWith(ChromeStubServer.ServerName) == true, st1?["server"]?.ToString());
            if (st1?["server"]?.ToString().StartsWith(ChromeStubServer.ServerName) != true) return;

            Check($"every one of the {expected.Count} fixture tools is listed as chrome__<tool>",
                  expected.All(names.Contains), string.Join(", ", expected.Where(e => !names.Contains(e))));
            Check("chrome__file_upload is NOT listed (in either spelling)",
                  !names.Any(n => n.EndsWith("file_upload", StringComparison.OrdinalIgnoreCase)));
            var nav = tools.FirstOrDefault(t => t["name"]?.ToString() == "chrome__navigate");
            var navFixture = fixture!.OfType<JObject>().First(t => t["name"]?.ToString() == "navigate");
            Check("inputSchema is the server's own, unchanged", nav != null && JToken.DeepEquals(nav["inputSchema"], navFixture["inputSchema"]));

            // The owner's rule where an agent reads the tool: every chrome__
            // description keeps the "[chrome bridge] " prefix (CluadeX keys on
            // it) and ENDS with the privacy rule.
            var chromeTools = tools.Where(t => t["name"]!.ToString().StartsWith("chrome__")).ToList();
            Check("every chrome__ description keeps the '[chrome bridge] ' prefix and ends with the privacy rule",
                  chromeTools.Count == expected.Count && chromeTools.All(t =>
                      t["description"]!.ToString().StartsWith("[chrome bridge] ", StringComparison.Ordinal)
                      && t["description"]!.ToString().EndsWith(ChromeBridgeIds.PrivacyRule, StringComparison.Ordinal)),
                  chromeTools.Select(t => t["description"]!.ToString()).FirstOrDefault(d => !d.EndsWith(ChromeBridgeIds.PrivacyRule)));
            Check("(control) a brain tool's description does not carry it",
                  tools.First(t => t["name"]!.ToString() == "brain_search")["description"]!.ToString().Contains(ChromeBridgeIds.PrivacyRule) == false);
            Check("private_browse_search's description carries the rule and asks clients for ask-every-time",
                  tools.FirstOrDefault(t => t["name"]?.ToString() == "private_browse_search")?["description"]?.ToString() is { } psd
                  && psd.Contains(ChromeBridgeIds.PrivacyRule) && psd.Contains("ask-every-time"));

            // …and in the server instructions, inside rule 2 ("save it without
            // asking"), inside what Claude Code shows.
            var instr = s.Instructions;
            var rulesEnd = instr.IndexOf("Each tool's description carries its full protocol.", StringComparison.Ordinal);
            var clause = instr.IndexOf(ChromeBridgeIds.PrivacyRuleShort, StringComparison.Ordinal);
            var rule2 = instr.IndexOf("\n2. ", StringComparison.Ordinal);
            var rule3 = instr.IndexOf("\n3. ", StringComparison.Ordinal);
            Check("with the bridge on, rule 2 of the instructions carries the privacy exception, all rules inside 2,000 characters",
                  clause > rule2 && clause < rule3 && rulesEnd > 0 && rulesEnd < 2000,
                  $"clause at {clause}, rule 2 at {rule2}, rules end at {rulesEnd}");

            // ── snapshot the vault, then browse ──
            var before = SnapshotTree(vault);

            var tabs = await s.Call("chrome__tabs_context_mcp", new JObject { ["createIfEmpty"] = true });
            var navigated = await s.Call("chrome__navigate", new JObject { ["url"] = ChromeStubServer.MarkerUrl(m), ["tabId"] = 101 });
            var page = await s.Call("chrome__get_page_text", new JObject { ["tabId"] = 101 });
            var form = await s.Call("chrome__form_input", new JObject { ["ref"] = "ref_5", ["value"] = "FORM-VALUE-" + m, ["tabId"] = 101 });
            var shot = await s.Call("chrome__computer", new JObject { ["action"] = "screenshot", ["tabId"] = 101 });
            var probe = await s.Call("chrome__read_console_messages", new JObject { ["tabId"] = 101, ["pattern"] = "x" });
            var roster = await s.Call("chrome__list_connected_browsers");
            var flat = await s.Call("chrome_get_page_text", new JObject { ["tabId"] = 101 });
            var upload = await s.Call("chrome__file_upload", new JObject { ["paths"] = new JArray(@"C:\Users\x\.ssh\id_rsa"), ["ref"] = "ref_1", ["tabId"] = 101 });
            // Names in the namespace that name no tool: refused, forwarded
            // nowhere, and (checked by the vault snapshot below) not written
            // to the activity feed by the unknown-tool path.
            var bare1 = await s.Call("chrome_", new JObject { ["note"] = "QBARE1" + m });
            var bare2 = await s.Call("chrome__", new JObject { ["note"] = "QBARE2" + m });
            Check("'chrome_' and 'chrome__' (no tool) are refused, en + th, and never reach the server",
                  IsError(bare1) && IsError(bare2) && !ResultText(bare2).Contains("ok ") && ResultText(bare1).Contains("Claude in Chrome")
                  && ResultText(bare2).Contains("ไม่มีเครื่องมือ"),
                  ResultText(bare1) + " | " + ResultText(bare2));

            Check("tabs_context_mcp and navigate reach the server and come back",
                  ResultText(tabs).Contains(ChromeStubServer.MarkerUrl(m)) && ResultText(navigated).Contains("Navigated to " + ChromeStubServer.MarkerUrl(m)));
            Check("get_page_text returns the page (the CLIENT asked for it — capping is the only change)",
                  ResultText(page).Contains("PAGE-TEXT-" + m) && !IsError(page));
            Check("the Grok spelling chrome_get_page_text reaches the same tool", ResultText(flat).Contains("PAGE-TEXT-" + m));

            var img = (shot?["result"]?["content"] as JArray)?.OfType<JObject>().FirstOrDefault(b => b["type"]?.ToString() == "image");
            Check("the screenshot comes back as an MCP image block, base64 and mimeType byte-for-byte as the server sent them",
                  img != null && img["data"]?.ToString() == ChromeStubServer.ScreenshotBase64(m) && img["mimeType"]?.ToString() == "image/png",
                  shot?.ToString(Newtonsoft.Json.Formatting.None)[..Math.Min(300, shot?.ToString().Length ?? 0)]);
            Check("…with no bus/task/cowork notice appended to a private result",
                  (shot?["result"]?["content"] as JArray)?.Count == 2);

            Check("the chrome child got NO CLAUDE_CODE_* / ANTHROPIC_* / CLAUDECODE from brainx-mcp's environment",
                  ResultText(probe).Contains("leaked=none"), ResultText(probe));
            Check("…and the flag was its first argument", ResultText(probe).Contains("args0=" + ChromeBridgeIds.McpFlag), ResultText(probe));

            Check("chrome__file_upload called by name is refused by the hub and never reaches the server",
                  IsError(upload) && !ResultText(upload).Contains("FILE-UPLOAD-REACHED") && ResultText(upload).Contains("file_upload"),
                  ResultText(upload));

            // ── NOTHING of it in the vault ── (snapshot BEFORE bridge_status:
            // that one is an ordinary brain tool and journals its own name)
            var after = SnapshotTree(vault);
            var changed = after.Where(kv => !before.TryGetValue(kv.Key, out var h) || h != kv.Value).Select(kv => kv.Key).ToList();
            // The session's own presence heartbeat runs on a timer whether or
            // not anything is browsed; it carries no tool name for a private call.
            var unexpected = changed.Where(p => !p.Replace('\\', '/').Contains(".obsidianx/agent-bus/presence/")).ToList();
            Check("no file in the vault was created or changed by the chrome calls (presence heartbeat aside)",
                  unexpected.Count == 0, string.Join(", ", unexpected));
            var needles = new[]
            {
                m, ChromeStubServer.ScreenshotBase64(m)[..40], "chrome__", "chrome_get", "get_page_text", "file_upload",
                "private-" + m.ToLowerInvariant(), "QBARE1", "QBARE2",
            };
            var leaks = FilesContaining(vault, needles);
            Check("no byte of the browsing — marker, URL, page text, typed value, screenshot, even a chrome tool name — is anywhere in the vault",
                  leaks.Count == 0, string.Join(" | ", leaks.Take(5)));
            Check("no journal (sessions/*.md) and no activity line was written",
                  !Directory.Exists(Path.Combine(vault, ".obsidianx", "sessions"))
                  || !Directory.GetFiles(Path.Combine(vault, ".obsidianx", "sessions")).Any(f => File.ReadAllText(f).Contains("chrome", StringComparison.OrdinalIgnoreCase)));

            var st2 = await s.Status();
            Check("bridge_status: Connected, a 'Test' call_tool action, en + th messages, the browsers array",
                  st2?["state"]?.ToString() == "Connected" && st2["action"]?["kind"]?.ToString() == "call_tool"
                  && st2["action"]?["target"]?.ToString() == "chrome__list_connected_browsers"
                  && !string.IsNullOrWhiteSpace(st2["message_th"]?.ToString()) && st2["browsers"] is JArray,
                  st2?.ToString(Newtonsoft.Json.Formatting.None));
            Check("…and it reports the private log, outside the vault",
                  st2?["privateLog"]?["available"]?.Value<bool>() == true
                  && string.Equals(st2["privateLog"]?["path"]?.ToString(), Path.GetFullPath(privateDir), StringComparison.OrdinalIgnoreCase));

            // Control: the journal IS live in this sandbox — an ordinary tool
            // (bridge_status, just called) lands in it. Without this, "no chrome
            // line in the journal" could pass merely because nothing journals.
            var sessionsDir = Path.Combine(vault, ".obsidianx", "sessions");
            Check("(control) an ordinary brain tool IS journaled in this same session",
                  Directory.Exists(sessionsDir) && Directory.GetFiles(sessionsDir).Any(f => File.ReadAllText(f).Contains("bridge_status")));
            Check("(control) …while still no chrome tool name is in that journal",
                  !Directory.GetFiles(sessionsDir).Any(f => File.ReadAllText(f).Contains("chrome", StringComparison.OrdinalIgnoreCase)));
            Check("(control) the vault snapshot covered real files", before.Count > 0, $"{before.Count} files");

            // ── all of it in the private store ──
            // (The log is written behind the calls; the search below waits
            // for the writer, so the file checks come after it.)
            var beforeSearch = SnapshotTree(vault);
            var found = await s.Call("private_browse_search", new JObject { ["query"] = "PAGE-TEXT-" + m });
            Check("the private store exists OUTSIDE the vault", File.Exists(Path.Combine(privateDir, "browse.db")));
            Check("the private folder is owner-only", new PrivateBrowseStore(privateDir).IsOwnerOnly());
            var body = JObject.Parse(ResultText(found));
            var first = (body["results"] as JArray)?.OfType<JObject>().FirstOrDefault();
            Check("private_browse_search finds the page text, tagged with the tool and the agent",
                  (int?)body["count"] >= 1 && first?["tool"]?.ToString() == "chrome__get_page_text" && first["agent"] != null,
                  body.ToString(Newtonsoft.Json.Formatting.None));
            var thai = JObject.Parse(ResultText(await s.Call("private_browse_search", new JObject { ["query"] = "ยอดเงินคงเหลือ" })));
            Check("…by a Thai phrase too", (int?)thai["count"] >= 1);
            var byUrl = JObject.Parse(ResultText(await s.Call("private_browse_search", new JObject { ["query"] = "private-" + m.ToLowerInvariant() })));
            Check("…and by URL, which the navigate entry carries",
                  (byUrl["results"] as JArray)?.OfType<JObject>().Any(r => r["tool"]?.ToString() == "chrome__navigate" && r["url"]?.ToString() == ChromeStubServer.MarkerUrl(m)) == true);
            var typed = JObject.Parse(ResultText(await s.Call("private_browse_search", new JObject { ["query"] = "FORM-VALUE-" + m })));
            Check("…and the typed form value", (int?)typed["count"] >= 1);
            var secret = JObject.Parse(ResultText(await s.Call("private_browse_search", new JObject { ["query"] = ChromeStubServer.SecretValue(m) })));
            Check("…but not the credential that was on the page (masked before storage)", (int?)secret["count"] == 0);
            var uploadRow = JObject.Parse(ResultText(await s.Call("private_browse_search", new JObject { ["query"] = "id_rsa" })));
            Check("a refused file_upload is not recorded as a browsing entry", (int?)uploadRow["count"] == 0);

            var shotRow = JObject.Parse(ResultText(await s.Call("private_browse_search", new JObject { ["query"] = "captured screenshot" })));
            var shotId = (shotRow["results"] as JArray)?.OfType<JObject>().SelectMany(r => r["screenshots"] as JArray ?? new JArray()).FirstOrDefault()?.ToString();
            var shotBack = await s.Call("private_browse_search", new JObject { ["screenshot_id"] = shotId });
            var backImg = (shotBack?["result"]?["content"] as JArray)?.OfType<JObject>().FirstOrDefault(b => b["type"]?.ToString() == "image");
            Check("the screenshot comes back from the private store byte-exact",
                  shotId != null && backImg?["data"]?.ToString() == ChromeStubServer.ScreenshotBase64(m), shotId);

            var after2 = SnapshotTree(vault);
            var changed2 = after2.Where(kv => !beforeSearch.TryGetValue(kv.Key, out var h) || h != kv.Value)
                                 .Select(kv => kv.Key).Where(p => !p.Replace('\\', '/').Contains(".obsidianx/agent-bus/presence/")).ToList();
            Check("the searches left no trace in the vault either (the query can be as private as the page)",
                  changed2.Count == 0 && FilesContaining(vault, new[] { "private_browse_search", "ยอดเงิน" }).Count == 0,
                  string.Join(", ", changed2));

            // ── switched off mid-session ──
            new ChromeBridgeSettings { Enabled = false, RetentionDays = 30 }.Save(privateDir);
            await Task.Delay(3500);   // the switch is re-read every 3 s
            var refused = await s.Call("chrome__navigate", new JObject { ["url"] = "https://example.test/" });
            Check("switched off mid-session: the next chrome call is refused with how to turn it back on (en + th)",
                  IsError(refused) && ResultText(refused).Contains("BrainX ▸ Settings") && ResultText(refused).Contains("ตั้งค่า"),
                  ResultText(refused));
            var st3 = await s.Status();
            Check("…and bridge_status says Disabled with the BrainX settings action",
                  st3?["state"]?.ToString() == "Disabled" && st3["action"]?["kind"]?.ToString() == "open_brainx_settings");
            Check("the stub really ran (a started file outside the vault)", File.Exists(started));
        }
        finally
        {
            if (s != null) await s.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task ChromeBridgeOffByDefault()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-chrome-off-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var privateDir = Path.Combine(root, "private");
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        BusSeal.EnsureKey(Path.Combine(root, "bus-seal.key"));
        var started = Path.Combine(root, "stub-started.txt");

        ChromeSession? s = null, noStub = null;
        try
        {
            // The stub is available — the only thing missing is the owner's yes.
            s = await StartChromeSession(exe, vault, privateDir, new Dictionary<string, string?>
            {
                ["BRAINX_CHROME_STUB_COMMAND"] = Environment.ProcessPath,
                [ChromeStubServer.EnvFlag] = "1",
                [ChromeStubServer.StartedEnv] = started,
            });
            var names = ((await s.List())?["result"]?["tools"] as JArray ?? new JArray()).Select(t => t["name"]!.ToString()).ToList();
            Check("no settings file → no chrome__ tool is listed", names.Count > 0 && !names.Any(n => n.StartsWith("chrome_")), $"{names.Count} tools");
            Check("…and no private_browse_search either: the bridge was never on and there is no log to search",
                  !names.Contains("private_browse_search"));
            Check("…and the instructions spend nothing on the privacy rule where no browsing is visible",
                  s.Instructions.Length > 0 && !s.Instructions.Contains(ChromeBridgeIds.PrivacyRuleShort));
            var st = await s.Status();
            Check("bridge_status: chrome entry present, Disabled, with the way to turn it on in en + th",
                  st?["state"]?.ToString() == "Disabled" && st["action"]?["kind"]?.ToString() == "open_brainx_settings"
                  && st["message_en"]!.ToString().Contains("off") && !string.IsNullOrWhiteSpace(st["message_th"]?.ToString()),
                  st?.ToString(Newtonsoft.Json.Formatting.None));
            // "Turn on in BrainX" is a command line the card can run — the
            // client opens Settings at the Claude in Chrome card — not the
            // brainx:// link nothing handled.
            var act = st?["action"] as JObject;
            var target = act?["target"]?.ToString() ?? "";
            var parsedBack = ClientLaunchArgs.Parse(SplitCommandLine(target).Skip(1));
            Check("…its target is BrainX.Client.exe --settings claude-in-chrome, with exe + args spelled out",
                  target.EndsWith(" --settings claude-in-chrome", StringComparison.Ordinal)
                  && target.Contains("BrainX.Client.exe", StringComparison.OrdinalIgnoreCase)
                  && act?["args"] is JArray a && a.Count == 2 && a[0]!.ToString() == "--settings" && a[1]!.ToString() == "claude-in-chrome"
                  && parsedBack.OpenSettings && parsedBack.VaultPath == null && ClientLaunchArgs.AnchorFor(parsedBack.SettingsSection) == "Chrome",
                  act?.ToString(Newtonsoft.Json.Formatting.None));
            var call = await s.Call("chrome__navigate", new JObject { ["url"] = "https://example.test/" });
            Check("calling chrome__navigate anyway is refused, not 'unknown tool'", IsError(call) && ResultText(call).Contains("off"), ResultText(call));
            Check("…and nothing was started — not even for discovery", !File.Exists(started));
            Check("…and no private store was created for an off bridge", !File.Exists(Path.Combine(privateDir, "browse.db")));

            // ON, but a sandbox with no stub: the real claude.exe is never looked for.
            new ChromeBridgeSettings { Enabled = true }.Save(privateDir);
            noStub = await StartChromeSession(exe, vault, privateDir, new Dictionary<string, string?>
            {
                ["BRAINX_CHROME_STUB_COMMAND"] = null,
            });
            var st2 = await noStub.Status();
            Check("a sandbox without a stub reports NoClaudeCode and names no path — the owner's claude.exe is never resolved",
                  st2?["state"]?.ToString() == "NoClaudeCode" && st2["claudePath"]?.Type == JTokenType.Null,
                  st2?.ToString(Newtonsoft.Json.Formatting.None));
            var call2 = await noStub.Call("chrome__tabs_context_mcp");
            Check("…and a chrome call there fails cleanly with a pointer to bridge_status", IsError(call2) && ResultText(call2).Contains("bridge_status"), ResultText(call2));
        }
        finally
        {
            if (s != null) await s.DisposeAsync();
            if (noStub != null) await noStub.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task ChromeRemoteChildRefuses()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-chrome-remote-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var privateDir = Path.Combine(root, "private");
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        BusSeal.EnsureKey(Path.Combine(root, "bus-seal.key"));
        new ChromeBridgeSettings { Enabled = true }.Save(privateDir);
        // A log with something in it, so a refusal is a refusal and not an empty result.
        new PrivateBrowseStore(privateDir).Record(new PrivateBrowseRecord { Tool = "get_page_text", ResultText = "QREMOTEQ7 must not be seen" });
        var started = Path.Combine(root, "stub-started.txt");

        ChromeSession? s = null;
        try
        {
            // Exactly how BrainX.Server's McpChild starts the remote child.
            s = await StartChromeSession(exe, vault, privateDir, new Dictionary<string, string?>
            {
                ["BRAINX_HEADLESS"] = "1",
                ["BRAINX_CHROME_STUB_COMMAND"] = Environment.ProcessPath,
                [ChromeStubServer.EnvFlag] = "1",
                [ChromeStubServer.StartedEnv] = started,
            }, bridges: false);
            var names = ((await s.List())?["result"]?["tools"] as JArray ?? new JArray()).Select(t => t["name"]!.ToString()).ToList();
            Check("the remote child does not list private_browse_search or any chrome__ tool",
                  names.Count > 0 && !names.Contains("private_browse_search") && !names.Any(n => n.StartsWith("chrome_")));
            var r = await s.Call("private_browse_search", new JObject { ["query"] = "QREMOTEQ7" });
            Check("…and refuses private_browse_search called by name, without revealing the log",
                  IsError(r) && ResultText(r).Contains("local-only") && !ResultText(r).Contains("must not be seen"), ResultText(r));
            var c = await s.Call("chrome__get_page_text", new JObject { ["tabId"] = 1, ["note"] = "QARGQ9" });
            Check("…and a chrome__ call is refused with no side effect", IsError(c) && !File.Exists(started), ResultText(c));
            // The bare namespace used to fall through to "unknown tool", which
            // writes an activity line (tool + error) into the vault.
            var bare = await s.Call("chrome_", new JObject { ["note"] = "QBAREQ8" });
            Check("…and so is the bare 'chrome_' (it names no tool, but it is in the namespace)", IsError(bare), ResultText(bare));
            // Control: an unknown NON-chrome name does reach the activity feed,
            // so "no chrome_ line in the vault" means something.
            await s.Call("not_a_tool_xyz", new JObject { ["note"] = "QCONTROLQ6" });
            Check("(control) an unknown non-chrome tool IS written to the activity feed in this session",
                  FilesContaining(vault, new[] { "not_a_tool_xyz" }).Count > 0);
            var leaks = FilesContaining(vault, new[] { "chrome__get_page_text", "QREMOTEQ7", "QARGQ9", "QBAREQ8", "\"chrome_\"", "unknown tool: chrome" });
            Check("…and none of the refused calls — names or arguments — were written into the vault (journal, activity feed)",
                  leaks.Count == 0, string.Join(" | ", leaks));
        }
        finally
        {
            if (s != null) await s.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ───────────── the discovery race (review 2026-10-10) ─────────────

    /// <summary>One request the stub received: when, which process, what.</summary>
    private sealed record StubHit(long Ticks, int Pid, string Method, string Tool);

    private static List<StubHit> ReadStubLog(string path)
    {
        var list = new List<StubHit>();
        if (!File.Exists(path)) return list;
        foreach (var l in File.ReadAllLines(path))
        {
            var p = l.Split(' ', 4);
            if (p.Length >= 3 && long.TryParse(p[0], out var t) && int.TryParse(p[1], out var pid))
                list.Add(new StubHit(t, pid, p[2], p.Length > 3 ? p[3].Trim() : ""));
        }
        return list;
    }

    /// <summary>
    /// THE BUG. A chrome__ call that arrived while discovery was spawning the
    /// server shared discovery's pipe; discovery then closed it under the call.
    /// The navigate RAN (the stub answered it) yet the agent was told "no
    /// response within 180s" after five seconds — its retry would click twice
    /// — and discovery's own failure was cached, so new sessions listed no
    /// chrome__ tools for ten minutes and bridge_status said "Connected · 0 tools".
    /// Run with a 3 s cold start (the review's repro) and with none.
    /// </summary>
    private static async Task ChromeDiscoveryRace()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        // Status first, on its own: while discovery's server is still cold,
        // bridge_status must answer at once (it used to queue behind the
        // spawn's lock for the whole cold start) and say Connecting.
        {
            var (root, vault, privateDir) = ChromeSandboxDirs("brainx-chrome-status-");
            ChromeSession? s = null;
            try
            {
                s = await StartChromeSession(exe, vault, privateDir, StubEnv("S1", root, new() { [ChromeStubServer.StartDelayEnv] = "4000" }));
                var sw0 = Stopwatch.StartNew();
                var stConnecting = await s.Status();
                var statusMs = sw0.ElapsedMilliseconds;
                Check("bridge_status answers within 2 s while discovery's server is still starting (4 s cold start), and says Connecting",
                      statusMs < 2000 && stConnecting?["state"]?.ToString() == "Connecting", $"{statusMs} ms, state {stConnecting?["state"]}");
                await s.List();   // discovery done
                var after = await s.Status();
                Check("(control) once discovery is in, it no longer says Connecting — the state came from the spawn in flight",
                      after?["state"]?.ToString() is { } st0 && st0 != "Connecting", after?["state"]?.ToString());
            }
            finally
            {
                if (s != null) await s.DisposeAsync();
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        // The race itself: the call goes out right after initialize, while
        // discovery is still spawning (3 s cold start) or racing it (none).
        foreach (var startMs in new[] { 3000, 0 })
        {
            var (root, vault, privateDir) = ChromeSandboxDirs("brainx-chrome-race-");
            var m = "R" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var log = Path.Combine(root, "stub-requests.log");
            ChromeSession? s = null;
            try
            {
                s = await StartChromeSession(exe, vault, privateDir, StubEnv(m, root, new()
                {
                    [ChromeStubServer.StartDelayEnv] = startMs.ToString(),
                    [ChromeStubServer.LogEnv] = log,
                }));

                var url = ChromeStubServer.MarkerUrl(m);
                var sentTicks = DateTime.UtcNow.Ticks;
                var sw = Stopwatch.StartNew();
                var nav = await s.Call("chrome__navigate", new JObject { ["url"] = url, ["tabId"] = 101 });
                var navMs = sw.ElapsedMilliseconds;

                Check($"[{startMs} ms start] the first chrome__navigate, sent during discovery, comes back as the SUCCESS it was",
                      !IsError(nav) && ResultText(nav).Contains("Navigated to " + url), $"{navMs} ms: {ResultText(nav)}");

                // Let discovery finish (tools/list waits for it), then read the stub's log.
                var names = ((await s.List())?["result"]?["tools"] as JArray ?? new JArray()).Select(t => t["name"]!.ToString()).ToList();
                var hits = ReadStubLog(log);
                var navs = hits.Where(h => h.Method == "tools/call" && h.Tool == "navigate").ToList();
                Check($"[{startMs} ms start] the server received that navigate exactly ONCE (no retry clicks twice)",
                      navs.Count == 1, string.Join("; ", hits.Select(h => $"{h.Pid}:{h.Method}:{h.Tool}")));
                var discoveryPids = hits.Where(h => h.Method == "tools/list").Select(h => h.Pid).ToHashSet();
                Check($"[{startMs} ms start] …served by a server of its own, never discovery's pipe",
                      navs.Count == 1 && discoveryPids.Count > 0 && !discoveryPids.Contains(navs[0].Pid),
                      $"navigate pid {navs.FirstOrDefault()?.Pid}, discovery pids {string.Join(",", discoveryPids)}");
                if (startMs > 0)
                {
                    var listAt = hits.Where(h => h.Method == "tools/list").Select(h => h.Ticks).DefaultIfEmpty(0).Min();
                    Check($"(control) [{startMs} ms start] discovery really was in flight when the call went out (its tools/list came later)",
                          listAt > sentTicks, $"tools/list {TimeSpan.FromTicks(listAt - sentTicks).TotalMilliseconds:0} ms after the call");
                }

                Check($"[{startMs} ms start] discovery survived: this session lists the chrome__ tools",
                      names.Contains("chrome__navigate") && names.Contains("chrome__get_page_text"), $"{names.Count(n => n.StartsWith("chrome__"))} chrome tools");
                var cache = File.Exists(Path.Combine(privateDir, "chrome-tools.cache.json"))
                    ? JObject.Parse(File.ReadAllText(Path.Combine(privateDir, "chrome-tools.cache.json")))["bridges"]?["chrome"] as JObject
                    : null;
                var cacheText = cache?.ToString(Newtonsoft.Json.Formatting.None) ?? "(no cache)";
                Check($"[{startMs} ms start] …and the private cache new sessions read holds the tools, not a failure",
                      cache?["tools"] is JArray { Count: > 0 } && cache["failedUtc"]?.Type is null or JTokenType.Null,
                      cacheText[..Math.Min(200, cacheText.Length)]);
                var st = await s.Status();
                Check($"[{startMs} ms start] bridge_status: Connected with the real tool count (not 'Connected · 0 tools')",
                      st?["state"]?.ToString() == "Connected" && (int?)st["tools"] > 0, $"{st?["state"]} · {st?["tools"]} tools");
            }
            finally
            {
                if (s != null) await s.DisposeAsync();
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    /// <summary>
    /// A server that dies mid-call. The call failed after a second or two but
    /// said "no response within 180s" — which sent the reader looking for a
    /// hang. It must say what happened and when, in both languages, and the
    /// next call must get a fresh server.
    /// </summary>
    private static async Task ChromePipeClosedEarly()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        var (root, vault, privateDir) = ChromeSandboxDirs("brainx-chrome-pipe-");
        var m = "P" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        ChromeSession? s = null;
        try
        {
            s = await StartChromeSession(exe, vault, privateDir, StubEnv(m, root, new() { [ChromeStubServer.ExitOnEnv] = "navigate" }));
            if (!await StubProven(s)) return;
            await s.List();   // discovery done

            var sw = Stopwatch.StartNew();
            var r = await s.Call("chrome__navigate", new JObject { ["url"] = ChromeStubServer.MarkerUrl(m) });
            var ms = sw.ElapsedMilliseconds;
            var text = ResultText(r);
            Check("the call fails (the server is gone) within seconds, not after the 180 s timeout", IsError(r) && ms < 30_000, $"{ms} ms");
            Check("…saying the server closed the pipe after N s — not 'no response within 180s'",
                  System.Text.RegularExpressions.Regex.IsMatch(text, @"closed the pipe after \d+(\.\d)? s") && !text.Contains("no response within"), text);
            Check("…in Thai too, with the warning that the action may already have run",
                  text.Contains("ปิดการเชื่อมต่อหลังจาก") && text.Contains("อาจทำงานไปแล้ว"), text);

            var again = await s.Call("chrome__tabs_context_mcp");
            Check("the next call gets a fresh server and works", !IsError(again) && ResultText(again).Contains(ChromeStubServer.MarkerUrl(m)), ResultText(again));
        }
        finally
        {
            if (s != null) await s.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Keeping a call (redaction, FTS insert, DPAPI) cost 0.7-2.3 s per page
    /// INSIDE the call, before the agent saw its result. It is written behind
    /// the call now. Proven with a deliberately slow writer (sandbox-only
    /// knob): the call returns well before the writer could have finished,
    /// and a search right after still finds the page (it waits for the writer).
    /// </summary>
    private static async Task ChromePrivateLogBehindTheCall()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        // For the report: what one ~100 KB page costs to keep, measured here.
        var probeRoot = Path.Combine(Path.GetTempPath(), "brainx-chrome-cost-" + Guid.NewGuid().ToString("N"), "private");
        try
        {
            var store = new PrivateBrowseStore(probeRoot);
            var page = string.Concat(Enumerable.Range(0, 2200).Select(i => $"บรรทัด {i} lorem ipsum dolor sit amet\n"));
            store.Record(new PrivateBrowseRecord { Tool = "warmup", ResultText = "x" });
            var swr = Stopwatch.StartNew();
            store.Record(new PrivateBrowseRecord { Tool = "get_page_text", ResultText = page });
            Console.WriteLine($"         (one {page.Length / 1024} KB page costs {swr.ElapsedMilliseconds} ms to keep — now off the call path)");
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(probeRoot)!, true); } catch { } }

        const int writerDelayMs = 4000;
        var (root, vault, privateDir) = ChromeSandboxDirs("brainx-chrome-async-");
        var m = "A" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        ChromeSession? s = null;
        try
        {
            s = await StartChromeSession(exe, vault, privateDir, StubEnv(m, root, new() { ["BRAINX_PRIVATE_LOG_DELAY_MS"] = writerDelayMs.ToString() }));
            if (!await StubProven(s)) return;
            await s.List();   // discovery done
            // Warm: the server is up, so the timed call below is the call
            // alone, not a cold start.
            await s.Call("chrome__tabs_context_mcp");

            var sw = Stopwatch.StartNew();
            var r = await s.Call("chrome__get_page_text", new JObject { ["tabId"] = 1, [ChromeStubServer.BigKbArg] = 300 });
            var callMs = sw.ElapsedMilliseconds;
            var text = ResultText(r);
            Check($"a 300 KB page comes back without waiting for the private log (writer delayed {writerDelayMs} ms per entry)",
                  !IsError(r) && callMs < writerDelayMs - 1500 && text.Contains("PAGE-TEXT-" + m), $"{callMs} ms");
            Check("…cut at 256 KB with advice for a web page, in en + th — not Unity's 'one component, a specific path'",
                  text.Contains("use find / read_page with a ref") && text.Contains("ใช้ find / read_page กับ ref") && !text.Contains("one component"),
                  text.Length > 300 ? text[^300..] : text);

            var sw2 = Stopwatch.StartNew();
            var found = JObject.Parse(ResultText(await s.Call("private_browse_search", new JObject { ["query"] = "PAGE-TEXT-" + m })));
            var searchMs = sw2.ElapsedMilliseconds;
            Check("a search right after finds the page — it waits for the writer, so nothing captured is missed",
                  (int?)found["count"] == 1, found.ToString(Newtonsoft.Json.Formatting.None));
            Check("(control) the writer really was slow: that search had to wait for it",
                  searchMs > 1000, $"search took {searchMs} ms");
            var kept = new PrivateBrowseStore(privateDir).ReadEntry((long)found["results"]![0]!["id"]!)?.Result ?? "";
            Check("…and the log kept the page as the SERVER sent it (captured before the cut rewrote the agent's copy)",
                  kept.Contains(ChromeStubServer.BigFillMarker + " 100 ") && !kept.Contains("truncated by the chrome bridge"),
                  kept.Length > 200 ? kept[^200..] : kept);
        }
        finally
        {
            if (s != null) await s.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// private_browse_search used to be listed and callable everywhere: in a
    /// session that never had the bridge, and in broker / Mind runs that take
    /// orders from a task card or a peer — a prompt-injected agent there could
    /// dump the log and forward it with agent_send or cowork.
    /// </summary>
    private static async Task ChromePrivateSearchGating()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        // A: bridge OFF, but a log exists → the owner may still search it.
        var (rootA, vaultA, privA) = ChromeSandboxDirs("brainx-chrome-gate-a-", enabled: false);
        new PrivateBrowseStore(privA).Record(new PrivateBrowseRecord { Tool = "get_page_text", ResultText = "QGATEA1 page" });
        ChromeSession? s = null;
        try
        {
            s = await StartChromeSession(exe, vaultA, privA, StubEnv("GA", rootA));
            var names = ((await s.List())?["result"]?["tools"] as JArray ?? new JArray()).Select(t => t["name"]!.ToString()).ToList();
            Check("bridge off but a log on disk: private_browse_search is listed and finds it, chrome__ tools are not",
                  names.Contains("private_browse_search") && !names.Any(n => n.StartsWith("chrome_"))
                  && (int?)JObject.Parse(ResultText(await s.Call("private_browse_search", new JObject { ["query"] = "QGATEA1" })))["count"] == 1);
            var ins = s.Instructions;
            var rulesEnd = ins.IndexOf("Each tool's description carries its full protocol.", StringComparison.Ordinal);
            Check("…and the instructions carry the privacy rule, every rule still inside 2,000 characters",
                  ins.Contains(ChromeBridgeIds.PrivacyRuleShort) && rulesEnd > 0 && rulesEnd < 2000, $"rules end at {rulesEnd}");
        }
        finally
        {
            if (s != null) await s.DisposeAsync();
            try { Directory.Delete(rootA, recursive: true); } catch { }
        }

        // B / C: unattended runs — the broker's task runner and the Mind. The
        // switch is ON and the stub is available: the only difference from an
        // owner's session is the run's environment. Review 2026-10-10 (rev2):
        // these runs still listed and called chrome__* — the owner's logged-in
        // browser, pre-approved (`--allowedTools mcp__brainx-brain`, a codex
        // that bypasses approvals) and driven by whatever a peer put in the
        // task. Now the bridge is off for them before anything is spawned.
        foreach (var (label, envName, envValue) in new[] { ("broker run", "BRAINX_BROKER_RUN", "1"), ("Mind", "BRAINX_AS_AGENT", "mind") })
        {
            var (root, vault, priv) = ChromeSandboxDirs("brainx-chrome-gate-u-");
            new PrivateBrowseStore(priv).Record(new PrivateBrowseRecord { Tool = "get_page_text", ResultText = "QGATEU2 bank page" });
            var m = "U" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var started = Path.Combine(root, "stub-started.txt");
            var stubLog = Path.Combine(root, "stub-requests.log");
            var url = ChromeStubServer.MarkerUrl(m);
            ChromeSession? u = null, owner = null;
            try
            {
                u = await StartChromeSession(exe, vault, priv, StubEnv(m, root, new() { [envName] = envValue, [ChromeStubServer.LogEnv] = stubLog }));
                var names = ((await u.List())?["result"]?["tools"] as JArray ?? new JArray()).Select(t => t["name"]!.ToString()).ToList();
                var r = await u.Call("private_browse_search", new JObject { ["query"] = "QGATEU2" });
                Check($"{label}: private_browse_search is not listed, and called by name it is refused without the log (en + th)",
                      !names.Contains("private_browse_search") && IsError(r) && !ResultText(r).Contains("bank page")
                      && ResultText(r).Contains("owner only") && ResultText(r).Contains("เฉพาะเจ้าของ"), ResultText(r));
                Check($"{label}: no chrome__ tool is listed although the owner's switch is on",
                      names.Count > 0 && !names.Any(n => n.StartsWith("chrome_", StringComparison.OrdinalIgnoreCase)),
                      string.Join(",", names.Where(n => n.StartsWith("chrome_"))));
                var nav = await u.Call("chrome__navigate", new JObject { ["url"] = url, ["tabId"] = 1 });
                var flat = await u.Call("chrome_navigate", new JObject { ["url"] = url, ["tabId"] = 1 });
                Check($"{label}: chrome__navigate (and the flat chrome_navigate) called by name is refused, saying why in en + th",
                      IsError(nav) && IsError(flat) && ResultText(nav).Contains("unattended runs") && ResultText(nav).Contains("ไม่มีคนดู")
                      && !ResultText(nav).Contains("Navigated"), ResultText(nav));
                var st = await u.Status();
                Check($"{label}: bridge_status says Disabled for this run, why in en + th, with no settings button (turning it on would not help)",
                      st?["state"]?.ToString() == "Disabled" && st["action"]?["kind"]?.ToString() == "none"
                      && st["message_en"]!.ToString().Contains("unattended") && st["message_th"]!.ToString().Contains("ไม่มีคนดู")
                      && st["unattended"]?.Type == JTokenType.Boolean && (bool)st["unattended"]! && st["claudePath"]?.Type == JTokenType.Null,
                      st?.ToString(Newtonsoft.Json.Formatting.None));
                // Matched on the words every wording of the clause has, not on
                // today's constant, so an older build's clause is caught too.
                Check($"{label}: …and the instructions carry no privacy clause — nothing private is visible to this run",
                      u.Instructions.Length > 0 && !u.Instructions.Contains(ChromeBridgeIds.PrivacyRuleShort)
                      && !u.Instructions.Contains("private_browse_search output"));
                await u.DisposeAsync();
                u = null;
                Check($"{label}: nothing was spawned — the stub never started and received no request, not even discovery",
                      !File.Exists(started) && ReadStubLog(stubLog).Count == 0, $"started={File.Exists(started)}, requests={ReadStubLog(stubLog).Count}");
                Check($"{label}: …the refused calls are not in the private log either (one entry, the seeded one)",
                      RowCount(new PrivateBrowseStore(priv), m.ToLowerInvariant()) == 0 && new PrivateBrowseStore(priv).Stats().Entries == 1);
                var leaks = FilesContaining(vault, new[] { m, "chrome__navigate", "chrome_navigate" });
                Check($"{label}: …nor in the vault (no journal, no activity line)", leaks.Count == 0, string.Join(" | ", leaks));

                // Control: the SAME sandbox, switch and stub — an attended session.
                owner = await StartChromeSession(exe, vault, priv, StubEnv(m, root, new() { [ChromeStubServer.LogEnv] = stubLog }));
                var ownerNames = ((await owner.List())?["result"]?["tools"] as JArray ?? new JArray()).Select(t => t["name"]!.ToString()).ToList();
                var ownerNav = await owner.Call("chrome__navigate", new JObject { ["url"] = url, ["tabId"] = 1 });
                Check($"(control) {label}: an attended session in the same sandbox lists chrome__navigate and its call reaches the stub",
                      ownerNames.Contains("chrome__navigate") && !IsError(ownerNav) && ResultText(ownerNav).Contains("Navigated to " + url)
                      && File.Exists(started) && ReadStubLog(stubLog).Any(h => h.Method == "tools/call" && h.Tool == "navigate"),
                      ResultText(ownerNav));
            }
            finally
            {
                if (u != null) await u.DisposeAsync();
                if (owner != null) await owner.DisposeAsync();
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    /// <summary>Split a command line the way the card would: quotes group, spaces split.</summary>
    private static List<string> SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ' ' && !quoted) { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); } continue; }
            cur.Append(ch);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        return parts;
    }

    private static Task ChromeSettingsLaunchArgs()
    {
        var a = ClientLaunchArgs.Parse(new[] { "--settings", "claude-in-chrome" });
        Check("--settings claude-in-chrome → open Settings at the Chrome card, and NO vault path (it used to be the vault)",
              a.OpenSettings && a.SettingsSection == "claude-in-chrome" && a.VaultPath == null && ClientLaunchArgs.AnchorFor(a.SettingsSection) == "Chrome",
              a.ToString());
        var b = ClientLaunchArgs.Parse(new[] { @"D:\Vaults\Mine" });
        Check("(control) a plain path is still the vault, and opens no settings", b.VaultPath == @"D:\Vaults\Mine" && !b.OpenSettings);
        var c = ClientLaunchArgs.Parse(new[] { @"D:\V", "--settings=CHROME" });
        Check("vault + --settings=<section> together, case-insensitive", c.VaultPath == @"D:\V" && c.OpenSettings && c.SettingsSection == "chrome");
        var d = ClientLaunchArgs.Parse(new[] { "--settings", "../../etc", "--veloapp-firstrun" });
        Check("an unknown section opens Settings itself; any other switch is never a vault",
              d.OpenSettings && d.SettingsSection == null && d.VaultPath == null);

        var msg = ClientLaunchArgs.ToMessage("claude-in-chrome");
        Check("the second launch's message round-trips", ClientLaunchArgs.FromMessage(msg) == (true, "claude-in-chrome"));
        Check("a foreign WM_COPYDATA is ignored; an unknown section opens nothing in particular",
              ClientLaunchArgs.FromMessage("format c:") == (false, null)
              && ClientLaunchArgs.FromMessage("brainx:settings:SettingsAnchorAbout\\..\\x") == (true, null)
              && ClientLaunchArgs.FromMessage(new string('x', 4000)) == (false, null));

        // Every section the switch can name has its card (and Chrome its jump
        // button) in the real MainWindow.xaml — no dead targets.
        var xaml = FindRepoFile("BrainX.Client", "MainWindow.xaml");
        if (xaml == null) { Check("BrainX.Client/MainWindow.xaml found", false); return Task.CompletedTask; }
        var text = File.ReadAllText(xaml);
        var missing = ClientLaunchArgs.Sections.Values.Distinct().Where(tag => !text.Contains($"x:Name=\"SettingsAnchor{tag}\"")).ToList();
        Check("every --settings section has its SettingsAnchor card in MainWindow.xaml", missing.Count == 0, string.Join(", ", missing));
        Check("…and Claude in Chrome has a jump button in the Settings menu", text.Contains("x:Name=\"SettingsNavChrome\"") && text.Contains("Tag=\"Chrome\""));

        var st = ChromeBridgeStatus.Build(new ChromeBridgeStatus.Inputs { Enabled = false, BrainXClientExe = @"C:\Users\x\AppData\Local\BrainX\current\BrainX.Client.exe" });
        Check("Disabled → open_brainx_settings with the client's quoted exe and the switch",
              st["action"]?["kind"]?.ToString() == "open_brainx_settings"
              && st["action"]?["target"]?.ToString() == "\"C:\\Users\\x\\AppData\\Local\\BrainX\\current\\BrainX.Client.exe\" --settings claude-in-chrome"
              && st["action"]?["exe"]?.ToString() == @"C:\Users\x\AppData\Local\BrainX\current\BrainX.Client.exe",
              st["action"]?.ToString(Newtonsoft.Json.Formatting.None));
        return Task.CompletedTask;
    }

    // ───────────── the instructions head, worst case (review rev2) ─────────────

    /// <summary>
    /// The head of the server instructions had about 2 characters to spare on
    /// a CI-length temp path, and whenever the hook warning sat in it (a list
    /// of problems with script names, ~400 characters) rules 6 and 7 fell past
    /// the 2,048 characters Claude Code shows. Measured here with everything
    /// that lengthens it at once: the hook warning (a fake broken ~/.claude —
    /// a sandbox-only knob), the chrome bridge on (the privacy clause) and a
    /// vault path of 150+ characters.
    /// </summary>
    private static async Task ChromeInstructionsWorstCase()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-chrome-head-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "a-vault-folder-long-enough-to-stand-in-for-a-ci-temp-path-or-a-onedrive-documents-folder", "vault");
        var privateDir = Path.Combine(root, "private");
        var claudeDir = Path.Combine(root, "claude");
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        Directory.CreateDirectory(claudeDir);
        BusSeal.EnsureKey(Path.Combine(Path.GetDirectoryName(vault)!, "bus-seal.key"));
        new ChromeBridgeSettings { Enabled = true, RetentionDays = 30 }.Save(privateDir);

        // The 2026-09-25 signature ($env: without "shell": "powershell") and
        // four scripts that are gone — a long problem list.
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
        var fullLine = HookHealth.InstructionsLine(problems);

        ChromeSession? s = null, healthy = null;
        try
        {
            s = await StartChromeSession(exe, vault, privateDir, StubEnv("H1", root, new() { ["BRAINX_SANDBOX_CLAUDE_DIR"] = claudeDir }));
            var ins = s.Instructions;
            var rulesEnd = ins.IndexOf("Each tool's description carries its full protocol.", StringComparison.Ordinal);
            var alarmAt = ins.IndexOf(HookHealth.HeadAlarm, StringComparison.Ordinal);
            var privAt = ins.IndexOf(ChromeBridgeIds.PrivacyRuleShort, StringComparison.Ordinal);
            Check($"(control) the worst case is real: {problems.Count} hook problems, a {vault.Length}-character vault path, and the head carries both the hook warning and the privacy clause",
                  problems.Count >= 2 && vault.Length >= 150 && alarmAt >= 0 && alarmAt < rulesEnd && privAt > 0 && privAt < rulesEnd,
                  $"problems {problems.Count}, alarm at {alarmAt}, privacy at {privAt}, rules end at {rulesEnd}");
            Check($"every rule ends inside 2,000 characters — at {rulesEnd} (Claude Code cuts at 2,048) — with the hook warning in front of them",
                  rulesEnd > 0 && rulesEnd < 2000 && alarmAt >= 0 && alarmAt + HookHealth.HeadAlarm.Length < rulesEnd, $"rules end at {rulesEnd}");

            // This vault is tiny; the owner's brain's size line is longer.
            const string intro = "The owner's personal brain";
            const string bigBrain = " — 123,456 notes, 123,456,789 words, 1,234,567 links";
            var introAt = ins.IndexOf(intro, StringComparison.Ordinal);
            var colon = introAt < 0 ? -1 : ins.IndexOf(':', introAt);
            var sizeLen = colon < 0 ? 0 : colon - (introAt + intro.Length);
            var bigEnd = rulesEnd - sizeLen + bigBrain.Length;
            Check($"…and on a brain of 123,456 notes they would end at {bigEnd}: still under 2,000",
                  introAt > 0 && colon > introAt && bigEnd < 2000, $"size line here {sizeLen} chars");

            var tailAt = Math.Max(0, rulesEnd);
            Check("nothing is lost: the vault path and every hook problem follow the rules, and the path is not in the head",
                  rulesEnd > 0
                  && ins.IndexOf("• Vault: " + vault, StringComparison.Ordinal) > rulesEnd
                  && problems.All(p => ins.IndexOf(p, rulesEnd, StringComparison.Ordinal) > rulesEnd)
                  && !ins[..rulesEnd].Contains(vault, StringComparison.OrdinalIgnoreCase),
                  ins[tailAt..Math.Min(ins.Length, tailAt + 600)]);

            var oldEnd = rulesEnd - (HookHealth.HeadAlarm.Length + 2) + (fullLine.Length + 2) + $" (vault: {vault})".Length;
            Check($"(control) laid out as before — the whole hook line and the path in the head — rule 7 would end at {oldEnd}, past the 2,048 Claude Code shows",
                  oldEnd > 2048, $"{oldEnd}");

            healthy = await StartChromeSession(exe, vault, privateDir, StubEnv("H2", root));
            Check("(control) the same sandbox without the fake Claude folder has no hook warning — the knob is what put it there",
                  healthy.Instructions.Length > 0 && !healthy.Instructions.Contains("HOOKS BROKEN") && !healthy.Instructions.Contains("HOOK PROBLEMS"));
        }
        finally
        {
            if (s != null) await s.DisposeAsync();
            if (healthy != null) await healthy.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ───────────── capture is bounded (review rev2) ─────────────

    /// <summary>
    /// The private log writer's queue is bounded at 64 entries — but each
    /// entry used to hold the WHOLE result: every text block and every image
    /// decoded, clipped only later by the store (200 K characters, 8 shots of
    /// at most 15 MB). A run of big pages behind a slow disk could hold
    /// hundreds of MB. Capture now keeps only what the store can keep, and
    /// what is stored must not change by a byte.
    /// </summary>
    private static Task ChromeCaptureIsBounded()
    {
        var m = "C" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var rnd = new Random(11);
        // A 5 MB page; a credential that straddles the 200 K cut (only the
        // redaction margin past the cut catches it whole).
        var sb = new StringBuilder("CAPTEXT-" + m + " ");
        while (sb.Length < PrivateBrowseStore.MaxResultChars - 16) sb.Append("word").Append(rnd.Next(100000)).Append(' ');
        sb.Append(" password=hunter2-" + m + " ");
        while (sb.Length < 5 * 1024 * 1024) sb.Append("fill").Append(rnd.Next(100000)).Append(' ');
        var page = sb.ToString();

        byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
        var big = Bytes((int)PrivateBrowseStore.MaxShotBytes + 1024 * 1024, 1);          // 16 MB: over the per-shot limit
        var smalls = Enumerable.Range(0, 12).Select(i => Bytes(2048 + i, 100 + i)).ToList();
        var content = new JArray
        {
            new JObject { ["type"] = "image", ["data"] = "not base64 !!", ["mimeType"] = "image/png" },
            new JObject { ["type"] = "text", ["text"] = page },
            new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(big), ["mimeType"] = "image/png" },
        };
        foreach (var b in smalls) content.Add(new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(b), ["mimeType"] = "image/jpeg" });
        content.Add(new JObject { ["type"] = "text", ["text"] = "TAIL-" + m });
        var result = new JObject { ["content"] = content };
        var args = new JObject { ["url"] = ChromeStubServer.MarkerUrl(m), ["tabId"] = 1, ["text"] = new string('a', 60_000) };
        var ts = DateTime.UtcNow;

        // What AfterCall captured before this fix, line for line.
        PrivateBrowseRecord OldCapture(JObject a, JObject r)
        {
            var rec = new PrivateBrowseRecord
            {
                TsUtc = ts, Agent = "agent", Tool = "get_page_text", Url = PrivateBrowseCapture.FirstUrl(a),
                ArgsJson = a.ToString(Newtonsoft.Json.Formatting.None), IsError = false,
            };
            var t = new StringBuilder();
            foreach (var block in (r["content"] as JArray)!.OfType<JObject>())
            {
                var type = block["type"]?.ToString();
                if (type == "text") t.AppendLine(block["text"]?.ToString());
                else if (type == "image" && block["data"]?.Type == JTokenType.String)
                {
                    try { rec.Images.Add((block["mimeType"]?.ToString() ?? "image/png", Convert.FromBase64String(block["data"]!.ToString()))); }
                    catch (FormatException) { }
                }
            }
            rec.ResultText = t.ToString();
            return rec;
        }
        static long Held(PrivateBrowseRecord r) =>
            2L * ((r.ResultText?.Length ?? 0) + (r.ArgsJson?.Length ?? 0) + (r.Url?.Length ?? 0)) + r.Images.Sum(i => (long)i.Data.Length);

        var old = OldCapture(args, result);
        var now = PrivateBrowseCapture.FromCall("get_page_text", args, result, null, "agent", ts);
        Check($"(control) captured the old way, this one call held {Held(old) / (1024 * 1024)} MB in the writer's queue: the whole 5 MB page, 13 images, one of 16 MB",
              old.ResultText!.Length > 5_000_000 && old.Images.Count == 13 && old.Images.Max(i => i.Data.Length) > PrivateBrowseStore.MaxShotBytes);
        Check($"captured now, it holds {Held(now) / 1024} KB: text and args cut to the store's clip + margin, the first 8 images, none over 15 MB",
              now.ResultText!.Length <= PrivateBrowseStore.CaptureChars(PrivateBrowseStore.MaxResultChars)
              && now.ArgsJson!.Length <= PrivateBrowseStore.CaptureChars(PrivateBrowseStore.MaxArgsChars)
              && now.Images.Count <= PrivateBrowseStore.MaxShotsPerEntry
              && now.Images.All(i => i.Data.Length <= PrivateBrowseStore.MaxShotBytes)
              && Held(now) < 1_000_000,
              $"text {now.ResultText.Length}, args {now.ArgsJson?.Length}, images {now.Images.Count}");

        var root = Path.Combine(Path.GetTempPath(), "brainx-chrome-capture-" + Guid.NewGuid().ToString("N"), "private");
        try
        {
            var store = new PrivateBrowseStore(root) { RetentionOverride = 30 };
            var idOld = store.Record(old);
            var idNow = store.Record(now);
            var eOld = store.ReadEntry(idOld);
            var eNow = store.ReadEntry(idNow);
            var hits = store.Search("CAPTEXT-" + m, 10);
            var hOld = hits.FirstOrDefault(h => h.Id == idOld);
            var hNow = hits.FirstOrDefault(h => h.Id == idNow);
            byte[]? Shot(string id) => store.ReadShot(id, out _);
            var shotsSame = hOld != null && hNow != null && hOld.Shots.Count == hNow.Shots.Count
                            && hOld.Shots.Zip(hNow.Shots).All(p => Shot(p.First.Id) is { } a && Shot(p.Second.Id) is { } b && a.AsSpan().SequenceEqual(b));
            Check("what is stored is identical either way: text, args, URL and the screenshots byte for byte",
                  eOld != null && eNow != null && eOld.Value.Result == eNow.Value.Result && eOld.Value.Args == eNow.Value.Args
                  && hOld?.Url == hNow?.Url && shotsSame,
                  $"result {eOld?.Result?.Length}/{eNow?.Result?.Length}, args {eOld?.Args?.Length}/{eNow?.Args?.Length}, shots {hOld?.Shots.Count}/{hNow?.Shots.Count}");
            Check("…the 7 small screenshots after the oversized one, as before; the 16 MB one and the non-base64 one are not kept",
                  hNow != null && hNow.Shots.Count == 7 && Shot(hNow.Shots[0].Id) is { } first && first.AsSpan().SequenceEqual(smalls[0]),
                  $"{hNow?.Shots.Count} shots");
            Check("…and the credential straddling the 200 K cut is masked, the text marked clipped, nothing of it in the database",
                  eNow?.Result is { } kept && kept.Contains("password=***") && !kept.Contains("hunter2-" + m) && kept.EndsWith("(clipped)")
                  && !DbFilesContain(store, "hunter2-" + m),
                  eNow?.Result is { } k2 ? k2[^Math.Min(200, k2.Length)..] : "(none)");

            // Control: a small page is not touched at all.
            var small = new JObject { ["content"] = new JArray
            {
                new JObject { ["type"] = "text", ["text"] = "SMALL-" + m },
                new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(smalls[0]), ["mimeType"] = "image/png" },
            } };
            var oldSmall = OldCapture(args, small);
            var nowSmall = PrivateBrowseCapture.FromCall("get_page_text", args, small, null, "agent", ts);
            Check("(control) a small page is captured exactly as before — text and image",
                  nowSmall.ResultText == oldSmall.ResultText && nowSmall.Images.Count == 1 && nowSmall.Images[0].Data.AsSpan().SequenceEqual(smalls[0]));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); } catch { } }
        return Task.CompletedTask;
    }
}
