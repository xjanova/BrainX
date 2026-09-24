using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using BrainX.Server.Cloud;
using BrainX.Server.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// Follow-ups: the self-update asset split + repair decisions, the Unicode /
/// whitespace path rules, the status codes the client keys on, and the
/// CloudRoot ACL.
/// </summary>
internal static partial class Program
{
    private static void RegisterCloudFollowupChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("self-update planner: full asset preferred, small fallback, repair once per version, no retry storm", UpdatePlannerChecks));
        checks.Add(("cloud paths: trailing dot/space segments, edge whitespace, NFC identity end to end", CloudUnicodePathChecks));
        checks.Add(("cloud status codes: dead token = 401 UNAUTHORIZED everywhere, scope = 403 FORBIDDEN, manifest only valid paths", CloudStatusCodeChecks));
        checks.Add(("cloud root ACL: SYSTEM + Administrators only, applied only when the node keeps access", CloudRootAclChecks));
    }

    // ───────────────────────── self-update planner ─────────────────────────

    private static ReleaseInfo Rel(string tag, bool full = true, bool small = true)
    {
        var assets = new List<ReleaseAsset>();
        if (small) assets.Add(new ReleaseAsset(UpdatePlanner.SmallAsset, $"https://example.test/{tag}/small.zip", 60_000_000));
        if (full) assets.Add(new ReleaseAsset(UpdatePlanner.FullAsset, $"https://example.test/{tag}/full.zip", 110_000_000));
        return new ReleaseInfo(tag, assets);
    }

    private static Task UpdatePlannerChecks()
    {
        var now = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var complete = new InstallState(McpPresent: true, ManagerPresent: true);
        var noMcp = new InstallState(McpPresent: false, ManagerPresent: false);
        var noManager = new InstallState(McpPresent: true, ManagerPresent: false);
        var empty = new UpdateState();
        const string running = "2.0.500+abc1234";

        var p = UpdatePlanner.Decide(running, Rel("v2.0.501"), null, complete, empty, now);
        Check("newer release with both assets → update from the FULL package",
              p.Kind == UpdateKind.Update && p.Asset?.Name == UpdatePlanner.FullAsset, p.Reason);
        p = UpdatePlanner.Decide(running, Rel("v2.0.501", full: false), null, complete, empty, now);
        Check("newer release with only the small asset → update from the small one",
              p.Kind == UpdateKind.Update && p.Asset?.Name == UpdatePlanner.SmallAsset, p.Reason);
        p = UpdatePlanner.Decide(running, Rel("v2.0.501", full: false, small: false), null, complete, empty, now);
        Check("newer release with neither asset → nothing", p.Kind == UpdateKind.None, p.Reason);
        p = UpdatePlanner.Decide(running, Rel("v2.0.500"), null, complete, empty, now);
        Check("same version (build metadata ignored), complete install → nothing", p.Kind == UpdateKind.None, p.Reason);
        p = UpdatePlanner.Decide(running, Rel("v2.0.499"), null, noMcp, empty, now);
        Check("an OLDER latest release is never installed", p.Kind == UpdateKind.None, p.Reason);

        // The bootstrap: the old updater installed the small package → no mcp\.
        p = UpdatePlanner.Decide(running, Rel("v2.0.500"), null, noMcp, empty, now);
        Check("up to date but mcp\\ missing → repair from this version's full package",
              p.Kind == UpdateKind.Repair && p.Asset?.Name == UpdatePlanner.FullAsset && p.Release?.Tag == "v2.0.500"
              && p.Missing!.Contains(UpdatePlanner.McpRelativePath), p.Reason);
        p = UpdatePlanner.Decide(running, Rel("v2.0.500"), null, noMcp, empty with { FullPackageStagedFor = "2.0.500" }, now);
        Check("…at most once per version: after that, never again for 2.0.500 (no restart loop)",
              p.Kind == UpdateKind.None && p.Reason.Contains("already applied"), p.Reason);
        p = UpdatePlanner.Decide("2.0.501", Rel("v2.0.501"), null, noMcp, empty with { FullPackageStagedFor = "2.0.500" }, now);
        Check("…but the next version may repair again", p.Kind == UpdateKind.Repair, p.Reason);
        p = UpdatePlanner.Decide(running, Rel("v2.0.500"), null, noManager, empty, now);
        Check("manager\\ missing and the release has a full package → repair", p.Kind == UpdateKind.Repair
              && p.Missing!.SequenceEqual([UpdatePlanner.ManagerRelativePath]), p.Reason);
        p = UpdatePlanner.Decide(running, Rel("v2.0.500", full: false), null, noManager, empty, now);
        Check("manager\\ missing but the release has no full package → nothing (manager only counts once there is one)",
              p.Kind == UpdateKind.None, p.Reason);
        p = UpdatePlanner.Decide(running, Rel("v2.0.500", full: false), null, noMcp, empty, now);
        Check("mcp\\ missing and no full package anywhere → nothing to repair from", p.Kind == UpdateKind.None, p.Reason);
        p = UpdatePlanner.Decide(running, Rel("v2.0.499"), Rel("v2.0.500"), noMcp, empty, now);
        Check("the release of the RUNNING version is used for the repair even when 'latest' is older",
              p.Kind == UpdateKind.Repair && p.Release?.Tag == "v2.0.500", p.Reason);
        p = UpdatePlanner.Decide("2.6.0", Rel("v2.0.500"), null, noMcp, empty, now);
        Check("a dev build with no matching release → nothing", p.Kind == UpdateKind.None, p.Reason);

        Check("fetch the running version's release only when a repair could follow",
              UpdatePlanner.NeedsCurrentRelease(running, Rel("v2.0.499"), noMcp, empty)
              && !UpdatePlanner.NeedsCurrentRelease(running, Rel("v2.0.500"), noMcp, empty)     // latest IS it
              && !UpdatePlanner.NeedsCurrentRelease(running, Rel("v2.0.501"), noMcp, empty)     // update instead
              && !UpdatePlanner.NeedsCurrentRelease(running, Rel("v2.0.499"), complete, empty)
              && !UpdatePlanner.NeedsCurrentRelease(running, null, noMcp, empty with { FullPackageStagedFor = "2.0.500" })
              && UpdatePlanner.NeedsCurrentRelease(running, null, noMcp, empty));

        // A failed update must not re-download and restart right after every start.
        var tried = empty with { LastUpdateTarget = "2.0.501", LastUpdateUtc = now.AddHours(-1) };
        p = UpdatePlanner.Decide(running, Rel("v2.0.501"), null, complete, tried, now);
        Check("an update that did not take is not retried for 6 h", p.Kind == UpdateKind.None && p.Reason.Contains("not retrying"), p.Reason);
        p = UpdatePlanner.Decide(running, Rel("v2.0.501"), null, complete, tried with { LastUpdateUtc = now.AddHours(-7) }, now);
        Check("…and is retried after that", p.Kind == UpdateKind.Update, p.Reason);
        p = UpdatePlanner.Decide(running, Rel("v2.0.502"), null, complete, tried, now);
        Check("…while a NEWER release is installed at once", p.Kind == UpdateKind.Update && p.Release?.Tag == "v2.0.502", p.Reason);

        // After download: may the staged package be applied?
        var repair = UpdatePlanner.Decide(running, Rel("v2.0.500"), null, noMcp, empty, now);
        Func<string, bool> Has(params string[] files) => rel => files.Contains(rel);
        Check("a staged package without BrainX.Server.exe is never applied",
              UpdatePlanner.CheckStaged(repair, Has(UpdatePlanner.McpRelativePath)) is { } r1 && r1.Contains("not a node build"));
        Check("a repair package that fixes nothing is not applied (no restart for nothing)",
              UpdatePlanner.CheckStaged(repair, Has(UpdatePlanner.ServerExe)) is { } r2 && r2.Contains("nothing to repair"));
        Check("a repair package that brings mcp\\ is applied",
              UpdatePlanner.CheckStaged(repair, Has(UpdatePlanner.ServerExe, UpdatePlanner.McpRelativePath)) is null);
        var update = UpdatePlanner.Decide(running, Rel("v2.0.501"), null, complete, empty, now);
        Check("an update package with the server is applied", UpdatePlanner.CheckStaged(update, Has(UpdatePlanner.ServerExe)) is null);

        // GitHub's JSON.
        var parsed = UpdatePlanner.ParseRelease("""
            {"tag_name":"v2.0.777","assets":[
              {"name":"brainx-node-win-x64.zip","browser_download_url":"https://github.com/x/y/releases/download/v2.0.777/brainx-node-win-x64.zip","size":62000000},
              {"name":"brainx-node-full-win-x64.zip","browser_download_url":"https://github.com/x/y/releases/download/v2.0.777/brainx-node-full-win-x64.zip","size":111000000},
              {"name":"evil.zip","browser_download_url":"http://insecure.example/evil.zip","size":1}]}
            """);
        Check("a GitHub release parses: tag, both assets with sizes, a non-https asset dropped",
              parsed?.Tag == "v2.0.777" && parsed.Full?.Size == 111000000 && parsed.Small?.Size == 62000000 && parsed.Assets.Count == 2,
              parsed?.ToString());
        Check("a hostile tag is cut to version characters", UpdatePlanner.ParseRelease("""{"tag_name":"v1&calc","assets":[]}""")?.Tag == "v1calc");

        // The state file round-trips, and a corrupt one is treated as empty.
        var dir = Path.Combine(Path.GetTempPath(), "brainx-upd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "selfupdate-state.json");
            var s = new UpdateState("2.0.500", "2.0.501", now);
            s.Save(path);
            Check("the update state survives a restart", UpdateState.Load(path) == s, File.ReadAllText(path));
            File.WriteAllText(path, "{ not json");
            Check("a corrupt state file reads as empty (costs at most one more attempt)", UpdateState.Load(path) == new UpdateState());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        Check("install probe looks for mcp\\brainx-mcp.exe and manager\\BrainX.ServerManager.exe",
              UpdatePlanner.McpRelativePath == Path.Combine("mcp", "brainx-mcp.exe")
              && UpdatePlanner.ManagerRelativePath == Path.Combine("manager", "BrainX.ServerManager.exe"));
        return Task.CompletedTask;
    }

    // ───────────────────────── paths: whitespace + Unicode ─────────────────────────

    private static async Task CloudUnicodePathChecks()
    {
        foreach (var bad in new[] { "a./x.md", "a /x.md", "a /x.md", "a　/x.md", " x.md", "x.md ", " x.md", "\tx.md", "a/b./c.md" })
            Check($"refused: '{bad.Replace(" ", "<nbsp>").Replace("　", "<ideo-space>").Replace("\t", "<tab>")}'",
                  CloudPaths.Validate(CloudPaths.Normalize(bad)) is not null);
        Check("inner spaces stay legal ('a b/c d.md', 'x .md')",
              CloudPaths.Validate("a b/c d.md") is null && CloudPaths.Validate("x .md") is null);

        var nfc = "Café/한글 が.md";                         // Café/한글 が.md
        var nfd = nfc.Normalize(NormalizationForm.FormD);
        Check("(the decomposed spelling really differs)", nfd != nfc && nfd.Length > nfc.Length);
        Check("Normalize turns NFD into NFC", CloudPaths.Normalize(nfd) == nfc);
        Check("Validate refuses a path that was not normalised first", CloudPaths.Validate(nfd) is not null && CloudPaths.Validate(nfc) is null);
        Check("Thai is its own NFC form", CloudPaths.Normalize("โน้ต/บันทึก.md") == "โน้ต/บันทึก.md");

        await using var node = await CloudNode.StartAsync();
        const string key = "UNICODE-0000-0001";
        var token = await node.NewAccountAsync(key);
        var vault = node.VaultOf(key);

        var up = await node.Post("/api/cloud/notes", Files((nfd, "from a Mac")), token);
        var files = ManifestFiles(await node.Get("/api/cloud/manifest", token));
        Check("an NFD upload is stored and listed under its NFC name",
              up.Status == HttpStatusCode.OK && files.Count == 1 && files[0]["path"]?.ToString() == nfc
              && File.Exists(Path.Combine(vault, nfc.Replace('/', Path.DirectorySeparatorChar))), $"{up} / {files}");
        var again = await node.Post("/api/cloud/notes", Files((nfc, "from Windows")), token);
        files = ManifestFiles(await node.Get("/api/cloud/manifest", token));
        Check("the NFC spelling updates the SAME note (one entry, one file)",
              again.Status == HttpStatusCode.OK && files.Count == 1 && files[0]["sha256"]?.ToString() == Sha("from Windows")
              && Directory.GetFiles(Path.Combine(vault, "Café")).Length == 1, $"{again} / {files}");
        var both = await node.Post("/api/cloud/notes", Files((nfc, "x"), (nfd, "y")), token);
        Check("NFC and NFD of one path in one batch → 400 BAD_PATH (they are the same note)",
              both.Status == HttpStatusCode.BadRequest && both.Code == "BAD_PATH", both.ToString());
        var fetch = await node.Post("/api/cloud/notes/fetch", new { paths = new[] { nfd } }, token);
        Check("fetch by the NFD spelling finds it, and answers with the NFC path",
              (fetch.Body["files"] as JArray)?.Count == 1 && fetch.Body["files"]?[0]?["path"]?.ToString() == nfc
              && fetch.Body["files"]?[0]?["content"]?.ToString() == "from Windows", fetch.ToString());
        var del = await node.Post("/api/cloud/notes/delete", new { paths = new[] { nfd.ToUpperInvariant() } }, token);
        Check("delete by NFD in another case removes it", del.Body["deleted"]?.ToObject<int>() == 1
              && ManifestFiles(await node.Get("/api/cloud/manifest", token)).Count == 0, del.ToString());

        // A file the MCP child wrote in decomposed form, straight onto the disk.
        Directory.CreateDirectory(Path.Combine(vault, "Café".Normalize(NormalizationForm.FormD)));
        var diskNfd = Path.Combine(vault, nfd.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(diskNfd, "written by an agent", new UTF8Encoding(false));
        node.Cloud.Vaults.For(CloudIds.AccountIdFor(key)).MarkDirty();
        files = ManifestFiles(await node.Get("/api/cloud/manifest", token));
        Check("a note named in NFD on disk is listed under its NFC name", files.Count == 1 && files[0]["path"]?.ToString() == nfc, files.ToString());
        fetch = await node.Post("/api/cloud/notes/fetch", new { paths = new[] { nfc } }, token);
        Check("…fetch by NFC reads it", fetch.Body["files"]?[0]?["content"]?.ToString() == "written by an agent", fetch.ToString());
        var update = await node.Post("/api/cloud/notes", Files((nfc, "edited on Windows")), token);
        Check("…and an NFC upload overwrites THAT file instead of creating an NFC twin",
              update.Status == HttpStatusCode.OK && File.ReadAllText(diskNfd) == "edited on Windows"
              && Directory.GetFiles(Path.GetDirectoryName(diskNfd)!).Length == 1
              && ManifestFiles(await node.Get("/api/cloud/manifest", token)).Count == 1, update.ToString());
    }

    // ───────────────────────── status codes the client keys on ─────────────────────────

    private static async Task CloudStatusCodeChecks()
    {
        await using var node = await CloudNode.StartAsync();
        const string key = "STATUS-0000-0001";
        var device = await node.NewAccountAsync(key);
        var read = (await node.Post("/api/cloud/tokens", new { name = "r", scope = "read" }, device)).Body["token"]!.ToString();
        var api = (await node.Post("/api/cloud/tokens", new { name = "w", scope = "readwrite" }, device)).Body["token"]!.ToString();
        var revoked = (await node.Post("/api/cloud/tokens", new { name = "gone", scope = "readwrite" }, device)).Body;
        await node.Delete($"/api/cloud/tokens/{revoked["id"]}", device);

        var routes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, "/api/cloud/account", null),
            (HttpMethod.Post, "/api/cloud/logout", new { }),
            (HttpMethod.Get, "/api/cloud/tokens", null),
            (HttpMethod.Post, "/api/cloud/tokens", new { name = "x", scope = "read" }),
            (HttpMethod.Delete, "/api/cloud/tokens/ffffffffffff", null),
            (HttpMethod.Get, "/api/cloud/manifest", null),
            (HttpMethod.Post, "/api/cloud/notes", Files(("a.md", "x"))),
            (HttpMethod.Post, "/api/cloud/notes/delete", new { paths = new[] { "a.md" } }),
            (HttpMethod.Post, "/api/cloud/notes/fetch", new { paths = new[] { "a.md" } }),
            (HttpMethod.Post, "/api/cloud/reindex", new { }),
        };
        var deadTokens = new[] { revoked["token"]!.ToString(), "bxc_" + new string('Z', 43), "not-even-a-token", "" };
        var wrong = new List<string>();
        foreach (var (method, path, body) in routes)
            foreach (var t in deadTokens)
            {
                var r = await node.SendAsync(method, path, body, t.Length == 0 ? null : t, ip: "10.1.2." + (wrong.Count + deadTokens.Length));
                if (r.Status != HttpStatusCode.Unauthorized || r.Code != "UNAUTHORIZED") wrong.Add($"{method} {path}: {r}");
            }
        Check("every cloud route answers a revoked, unknown, malformed or missing token with 401 UNAUTHORIZED",
              wrong.Count == 0, string.Join(" | ", wrong));

        var forbidden = new[]
        {
            await node.Post("/api/cloud/notes", Files(("r.md", "x")), read),
            await node.Post("/api/cloud/notes/delete", new { paths = new[] { "r.md" } }, read),
            await node.Post("/api/cloud/reindex", new { }, read),
            await node.Get("/api/cloud/tokens", api),
            await node.Post("/api/cloud/tokens", new { name = "x", scope = "read" }, api),
            await node.Delete("/api/cloud/tokens/ffffffffffff", api),
        };
        Check("scope violations (read token writing, api token managing tokens) → 403 FORBIDDEN",
              forbidden.All(r => r.Status == HttpStatusCode.Forbidden && r.Code == "FORBIDDEN"),
              string.Join(" | ", forbidden.Select(r => r.ToString())));
        Check("the only other 401 is login's INVALID_LICENSE",
              (await node.Post("/api/cloud/login", new { licenseKey = "NOPE-0000-0001" })).Code == "INVALID_LICENSE");

        // The manifest never lists what the path rules would refuse.
        var vault = node.VaultOf(key);
        void Plant(string rel)
        {
            var full = Path.Combine(vault, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "planted");
        }
        Plant("ok/fine.md");
        Plant(".obsidianx/secret.md");
        Plant(".trash/old.md");
        Plant("notes.txt");
        Plant("ok/.hidden.md");
        Plant("ok/" + new string('n', 201) + ".md");
        node.Cloud.Vaults.For(CloudIds.AccountIdFor(key)).MarkDirty();
        var listed = ManifestFiles(await node.Get("/api/cloud/manifest", device)).Select(f => f["path"]!.ToString()).ToList();
        Check("the manifest lists only valid note paths (no dot folders, dot files, non-.md, over-long names)",
              listed.SequenceEqual(["ok/fine.md"]), string.Join(", ", listed));
    }

    // ───────────────────────── CloudRoot ACL ─────────────────────────

    private static Task CloudRootAclChecks()
    {
        Check("harden a folder the node just created", CloudRootAcl.ShouldHarden(createdNow: true, isProtected: false)
              && CloudRootAcl.ShouldHarden(createdNow: true, isProtected: true));
        Check("harden an existing folder that still inherits its permissions", CloudRootAcl.ShouldHarden(createdNow: false, isProtected: false));
        Check("never touch an existing folder whose ACL is explicit (the owner's choice)", !CloudRootAcl.ShouldHarden(createdNow: false, isProtected: true));
        Check("only SYSTEM or an elevated administrator may apply it (anyone else would lock themselves out)",
              CloudRootAcl.IdentityCanHarden(true, false) && CloudRootAcl.IdentityCanHarden(false, true) && !CloudRootAcl.IdentityCanHarden(false, false));

        var dir = Path.Combine(Path.GetTempPath(), "brainx-acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var (off, _) = CloudRootAcl.Apply(dir, enabled: false, createdNow: true);
            Check("disabled (the harness default) → nothing is changed", off == CloudRootAcl.Outcome.SkippedDisabled);
            if (!OperatingSystem.IsWindows())
            {
                var (nw, _) = CloudRootAcl.Apply(dir, enabled: true, createdNow: true);
                Check("not Windows → skipped", nw == CloudRootAcl.Outcome.SkippedNotWindows);
                return Task.CompletedTask;
            }
            AclWindowsChecks(dir);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static void AclWindowsChecks(string dir)
    {
        var sddl = CloudRootAcl.BuildSecurity().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Check("the ACL: protected (no inheritance from above), full control for SYSTEM and Administrators, inherited below",
              sddl.StartsWith("D:P", StringComparison.Ordinal) && sddl.Contains("(A;OICI;FA;;;SY)") && sddl.Contains("(A;OICI;FA;;;BA)")
              && sddl.Split('(').Length == 3, sddl);

        bool elevated;
        using (var me = WindowsIdentity.GetCurrent())
            elevated = me.IsSystem || new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        var (outcome, message) = CloudRootAcl.Apply(dir, enabled: true, createdNow: true);
        var probe = Path.Combine(dir, "still-mine.txt");
        var stillWritable = false;
        try { File.WriteAllText(probe, "x"); File.Delete(probe); stillWritable = true; } catch { }
        if (elevated)
        {
            var applied = new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access);
            Check("elevated harness: the ACL is applied for real, and the process keeps access",
                  outcome == CloudRootAcl.Outcome.Applied && applied.AreAccessRulesProtected && stillWritable, message);
            var (again, _) = CloudRootAcl.Apply(dir, enabled: true, createdNow: false);
            Check("…and a second start leaves the now-explicit ACL alone", again == CloudRootAcl.Outcome.AlreadyExplicit);
        }
        else
        {
            Check("non-elevated harness: skipped on identity, and the folder is still ours",
                  outcome == CloudRootAcl.Outcome.SkippedIdentity && stillWritable
                  && !new DirectoryInfo(dir).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected, message);
        }
    }
}
