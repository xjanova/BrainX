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
        Check("claude.ai → SignedIn",
              ClaudeAuthProbe.FromJson("""{"loggedIn":true,"authMethod":"claude.ai","email":"x@y","orgName":"o"}""")?.State == ClaudeAuthState.SignedIn);
        Check("console / apiKey → ApiKeyOnly",
              ClaudeAuthProbe.FromJson("""{"loggedIn":true,"authMethod":"console"}""")?.State == ClaudeAuthState.ApiKeyOnly
              && ClaudeAuthProbe.FromJson("""{"loggedIn":true,"authMethod":"apiKey"}""")?.State == ClaudeAuthState.ApiKeyOnly);
        Check("a banner before the JSON is tolerated",
              ClaudeAuthProbe.FromJson("warning: something\n{\"loggedIn\":true,\"authMethod\":\"oauth\"}")?.State == ClaudeAuthState.SignedIn);
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

            // Retention: an entry from 40 days ago goes at 30 days.
            store.Record(new PrivateBrowseRecord { TsUtc = DateTime.UtcNow.AddDays(-40), Tool = "navigate", Url = "https://old.example/", ResultText = "OLD-ENTRY" });
            Check("(the old entry is there before pruning)", store.Search("OLD-ENTRY").Count == 1);
            var removed = store.Prune(30);
            Check("pruning at 30 days removes the 40-day-old entry and keeps today's",
                  removed == 1 && store.Search("OLD-ENTRY").Count == 0 && store.Search("ยอดเงิน").Count == 1, $"removed {removed}");

            var cleared = store.Clear();
            Check("Clear removes every entry and screenshot", cleared == 1 && store.Search("").Count == 0
                  && (!Directory.Exists(store.ShotsDir) || Directory.GetFiles(store.ShotsDir).Length == 0), $"cleared {cleared}");
            var db = File.ReadAllBytes(store.DbPath);
            Check("…and the cleared text is not left in the database file (secure_delete + VACUUM)",
                  !Encoding.UTF8.GetString(db).Contains("PAGE-TEXT-" + m));
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); } catch { } }
        return Task.CompletedTask;
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
            ("ApiKeyOnly", In(auth: new ClaudeAuthResult(ClaudeAuthState.ApiKeyOnly, "console", "cli"))),
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

        var api = ChromeBridgeStatus.Build(In(auth: new ClaudeAuthResult(ClaudeAuthState.ApiKeyOnly, "console", "cli")));
        Check("ApiKeyOnly says plainly that an API key cannot drive Chrome (en + th), and offers 'auth login --claudeai'",
              api["message_en"]!.ToString().Contains("API key or API credit cannot drive Chrome")
              && api["message_th"]!.ToString().Contains("API key")
              && api["action"]!["kind"]!.ToString() == "run_console"
              && api["action"]!["target"]!.ToString().Contains("auth login --claudeai"));
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
        await Rpc(server, 1, "initialize", new JObject
        {
            ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JObject(),
            ["clientInfo"] = new JObject { ["name"] = "cluadex", ["version"] = "1" },
        });
        await server.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        return new ChromeSession { Server = server };
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
                "private-" + m.ToLowerInvariant(),
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
            var beforeSearch = SnapshotTree(vault);
            Check("the private store exists OUTSIDE the vault", File.Exists(Path.Combine(privateDir, "browse.db")));
            Check("the private folder is owner-only", new PrivateBrowseStore(privateDir).IsOwnerOnly());

            var found = await s.Call("private_browse_search", new JObject { ["query"] = "PAGE-TEXT-" + m });
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
            Check("…private_browse_search is still there locally (the owner may search an existing log)", names.Contains("private_browse_search"));
            var st = await s.Status();
            Check("bridge_status: chrome entry present, Disabled, with the way to turn it on in en + th",
                  st?["state"]?.ToString() == "Disabled" && st["action"]?["kind"]?.ToString() == "open_brainx_settings"
                  && st["message_en"]!.ToString().Contains("off") && !string.IsNullOrWhiteSpace(st["message_th"]?.ToString()),
                  st?.ToString(Newtonsoft.Json.Formatting.None));
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
            Check("…and neither refused call's arguments were written into the vault (journal, activity feed)",
                  FilesContaining(vault, new[] { "chrome__get_page_text", "QREMOTEQ7", "QARGQ9" }).Count == 0,
                  string.Join(" | ", FilesContaining(vault, new[] { "chrome__get_page_text", "QREMOTEQ7", "QARGQ9" })));
        }
        finally
        {
            if (s != null) await s.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
