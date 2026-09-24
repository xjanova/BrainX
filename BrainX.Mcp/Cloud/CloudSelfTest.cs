using System.Diagnostics;
using System.Text;
using BrainX.Core.Services.Cloud;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

/// <summary>
/// `brainx-mcp cloud selftest [--dir DIR] [--keep] [--no-mcp]` — hidden.
///
/// Checks the client half of BrainX Cloud without the real server:
///   1. pure rules  — path rules, hashing, CRLF/Thai/BOM round trips, batching,
///                    push and pull planning (delete-only-what-we-uploaded, …),
///                    the credential store (no key, no clear token on disk);
///   2. end to end  — CloudApiClient + CloudSyncEngine against CloudFakeServer
///                    (a loopback implementation of the contract): login errors,
///                    push/pull, no ping-pong, batching, cancel + resume,
///                    quota, expiry, offline, timeout, lock, tokens;
///   3. serve mode  — a real `brainx-mcp --serve --cloud` child: startup pull,
///                    a write tool pushed back to the cloud, offline start,
///                    and its log checked for the token.
/// Never touches the owner's sign-in or cache: every path is redirected into DIR.
/// </summary>
internal static class CloudSelfTest
{
    private static int _pass, _fail;
    private static bool _verbose;
    private static readonly List<string> Failures = new();

    private const string Key = "BXC-SELF-TEST-KEY1";
    private const string Key2 = "BXC-SELF-TEST-KEY2";
    private const string ExpiredKey = "BXC-SELF-TEST-OLD9";

    public static async Task<int> RunAsync(string[] args)
    {
        string? dirArg = null;
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--dir" && i + 1 < args.Length) dirArg = args[++i];
        var keep = args.Contains("--keep");
        _verbose = args.Contains("--verbose");
        var root = Path.Combine(dirArg ?? Path.GetTempPath(), "brainx-cloud-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        // Everything that could reach the owner's real state goes into root.
        Environment.SetEnvironmentVariable("BRAINX_CLOUD_STORE", Path.Combine(root, "store", "cloud.json"));
        Environment.SetEnvironmentVariable("BRAINX_CLOUD_CACHE_ROOT", Path.Combine(root, "cache"));
        Environment.SetEnvironmentVariable("BRAINX_CLOUD_URL", null);
        Environment.SetEnvironmentVariable("BRAINX_CLOUD_TOKEN", null);

        Console.WriteLine($"BrainX Cloud self-test · work dir {root}");
        var sw = Stopwatch.StartNew();
        try
        {
            Section("path rules"); PathRules();
            Section("hashing and disk round trips"); Hashing(root);
            Section("batching"); Batching();
            Section("push planning"); PushPlanning();
            Section("pull planning"); PullPlanning();
            Section("credential store"); CredentialStore(root);
            Section("end to end against the fake server"); await EndToEndAsync(root);
            Section("brainx-mcp cloud … commands"); await CliAsync(root);
            if (!args.Contains("--no-mcp"))
            {
                Section("brainx-mcp --cloud serve mode");
                await ServeModeAsync(root);
            }
        }
        catch (Exception ex)
        {
            Fail("self-test crashed", ex.ToString());
        }
        finally
        {
            if (!keep) { try { Directory.Delete(root, recursive: true); } catch { } }
        }

        Console.WriteLine();
        Console.WriteLine($"{_pass} passed, {_fail} failed · {sw.Elapsed.TotalSeconds:0.0}s");
        foreach (var f in Failures) Console.WriteLine("  FAILED: " + f);
        return _fail == 0 ? 0 : 1;
    }

    // ── tiny harness ─────────────────────────────────────────────────

    private static void Section(string name) { Console.WriteLine(); Console.WriteLine("── " + name); }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _pass++; Console.WriteLine("  PASS " + name); }
        else Fail(name, detail);
    }

    private static void Fail(string name, string? detail)
    {
        _fail++;
        Failures.Add(name);
        Console.WriteLine("  FAIL " + name + (detail != null ? " — " + detail : ""));
    }

    private static async Task ExpectCode(string name, string code, Func<Task> call)
    {
        try
        {
            await call();
            Fail(name, "no error; expected " + code);
        }
        catch (CloudApiException ex)
        {
            Check(name, ex.Code == code, $"got {ex.Code}, expected {code}");
        }
    }

    private static LocalNote N(string path, string content) => new()
    {
        Path = path,
        FullPath = "",
        Sha256 = CloudSyncEngine.Sha256Hex(content),
        Utf8Bytes = Encoding.UTF8.GetByteCount(content),
        WireBytes = Encoding.UTF8.GetByteCount(content),
    };

    private static CloudManifest M(params (string Path, string Content)[] files) => new()
    {
        Files = files.Select(f => new CloudManifestFile
        {
            Path = f.Path, Sha256 = CloudSyncEngine.Sha256Hex(f.Content), Size = Encoding.UTF8.GetByteCount(f.Content),
        }).ToList(),
    };

    private static string H(string s) => CloudSyncEngine.Sha256Hex(s);

    // ── 1. pure rules ────────────────────────────────────────────────

    private static void PathRules()
    {
        string[] good =
        {
            "Programming/foo.md", "root.md", "โน้ต/ภาษาไทย.md", "a b/c d.md", "A/B/C/D.MD", "x/y-z_1 (2).md",
        };
        foreach (var p in good) Check($"valid: {p}", CloudPathRules.IsValid(p), CloudPathRules.Validate(p));

        string[] bad =
        {
            "", "/x.md", "C:/x.md", "a\\b.md", "../x.md", "a/../b.md", ".obsidianx/x.md", "a/.git/x.md",
            "x.txt", "a/<b>.md", "a/b|c.md", "a/b?.md", "con.md", "a/COM1.md", "nul.txt.md", "a//b.md",
            "a /b.md", "a./b.md", "a/b\u0001.md", new string('x', 201) + ".md",
            string.Join("/", Enumerable.Repeat(new string('y', 150), 3)) + ".md",
        };
        foreach (var p in bad) Check($"rejected: {Printable(p)}", !CloudPathRules.IsValid(p));

        Check("CLAUDE.md at the root is machine-managed", CloudPathRules.IsMachineManaged("CLAUDE.md") && CloudPathRules.IsMachineManaged("claude.md"));
        Check("Notes/CLAUDE.md is ordinary", !CloudPathRules.IsMachineManaged("Notes/CLAUDE.md"));
        var cache = Path.Combine(Path.GetTempPath(), "cache-x");
        Check("ToLocalPath refuses an escape", CloudPathRules.ToLocalPath(cache, "../evil.md") == null);
        Check("ToLocalPath keeps a normal path inside",
              CloudPathRules.ToLocalPath(cache, "A/b.md") is { } lp && lp.StartsWith(Path.GetFullPath(cache), StringComparison.OrdinalIgnoreCase));
        Check("top folder of a root note is '/'", CloudPathRules.TopFolderOf("x.md") == CloudSyncState.RootToken);
    }

    private static string Printable(string s) =>
        s.Length > 40 ? s[..37] + "…" : new string(s.Select(c => c < 0x20 ? '·' : c).ToArray());

    private static void Hashing(string root)
    {
        Check("sha256('abc') matches the published vector",
              H("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        Check("CRLF and LF hash differently (no normalisation)", H("a\r\nb") != H("a\nb"));

        var dir = Path.Combine(root, "roundtrip");
        Directory.CreateDirectory(dir);
        var samples = new Dictionary<string, string>
        {
            ["lf"] = "# LF\nline\n",
            ["crlf"] = "# CRLF\r\nline\r\n",
            ["thai"] = "# ภาษาไทย\r\nเนื้อหา ✓ 🧠\n",
            ["bomchar"] = "\uFEFFstarts with a BOM character",
            ["empty"] = "",
        };
        foreach (var (name, content) in samples)
        {
            var f = Path.Combine(dir, name + ".md");
            File.WriteAllBytes(f, CloudSyncEngine.EncodeForDisk(content));
            var back = CloudSyncEngine.ReadText(f);
            Check($"disk round trip keeps the text exactly ({name})", back == content,
                  $"{back.Length} chars back vs {content.Length}");
        }

        // A note saved by an editor WITH a UTF-8 BOM: the text as read has no
        // BOM, and reading it twice gives the same hash — no phantom changes.
        var withBom = Path.Combine(dir, "editor-bom.md");
        File.WriteAllText(withBom, "saved with BOM\r\n", new UTF8Encoding(true));
        var t1 = CloudSyncEngine.ReadText(withBom);
        var t2 = CloudSyncEngine.ReadText(withBom);
        Check("an editor's BOM is not part of the text", t1 == "saved with BOM\r\n");
        Check("hash of a BOM file is stable", H(t1) == H(t2));

        var s = "a\"b\\c\r\n";
        Check("wire-size estimate equals Json.NET's escaped length",
              CloudSyncEngine.EstimateWireBytes(s, Encoding.UTF8.GetByteCount(s)) == JsonConvert.ToString(s).Length - 2);
    }

    private static void Batching()
    {
        var small = Enumerable.Range(0, 450).Select(i => i).ToList();
        var b = CloudSyncEngine.Batch(small, _ => 10_000, 200, CloudSyncEngine.MaxBatchBytes);
        Check("450 small notes → batches of 200/200/50", b.Select(x => x.Count).SequenceEqual(new[] { 200, 200, 50 }),
              string.Join(",", b.Select(x => x.Count)));
        var big = CloudSyncEngine.Batch(new[] { 4L << 20, 4L << 20, 4L << 20 }, x => x, 200, CloudSyncEngine.MaxBatchBytes);
        Check("three 4 MB notes → three batches (6 MB cap)", big.Count == 3);
        var mixed = CloudSyncEngine.Batch(new[] { 5L << 20, 2L << 20, 1L << 20 }, x => x, 200, CloudSyncEngine.MaxBatchBytes);
        Check("5 MB + 2 MB + 1 MB → [5] [2,1]", mixed.Select(x => x.Count).SequenceEqual(new[] { 1, 2 }),
              string.Join(",", mixed.Select(x => x.Count)));
        var huge = CloudSyncEngine.Batch(new[] { 7L << 20 }, x => x, 200, CloudSyncEngine.MaxBatchBytes);
        Check("an item over the byte cap still travels (alone)", huge.Count == 1 && huge[0].Count == 1);
    }

    private static void PushPlanning()
    {
        var state = new CloudSyncState { AccountId = "acc" };
        state.Uploaded["P/old.md"] = H("old");                 // ours, deleted locally      → delete
        state.Uploaded["P/same.md"] = H("same");               // identical                  → nothing
        state.Uploaded["P/edited-elsewhere.md"] = H("v1");     // cloud v2, ours still v1    → keep cloud's
        state.Uploaded["P/changed.md"] = H("c1");              // ours c2, cloud c1          → upload
        state.Uploaded["Q/deselected.md"] = H("q");            // folder Q unticked          → delete
        state.Uploaded["M/missing.md"] = H("m");               // folder M ticked but gone   → keep
        state.Uploaded["P/Case.md"] = H("case");               // renamed to P/case.md       → forget

        var scan = new LocalScan();
        scan.Notes.Add(N("P/new.md", "new"));
        scan.Notes.Add(N("P/same.md", "same"));
        scan.Notes.Add(N("P/edited-elsewhere.md", "v1"));
        scan.Notes.Add(N("P/changed.md", "c2"));
        scan.Notes.Add(N("P/case.md", "case"));
        scan.MissingFolders.Add("M");

        var manifest = M(("P/same.md", "same"), ("P/edited-elsewhere.md", "v2"), ("P/changed.md", "c1"),
                         ("P/old.md", "old"), ("Q/deselected.md", "q"), ("M/missing.md", "m"),
                         ("P/Case.md", "case"), ("Other/theirs.md", "another machine's note"));

        var plan = CloudSyncEngine.PlanPush(scan, new[] { "P", "M" }, manifest, state, allowDeletes: true, allowMassDelete: false);
        var uploads = plan.Uploads.Select(u => u.Path).OrderBy(x => x).ToList();
        Check("uploads = new + locally changed", uploads.SequenceEqual(new[] { "P/changed.md", "P/new.md" }), string.Join(",", uploads));
        Check("identical note is adopted, not re-sent", plan.Adopt.ContainsKey("P/same.md"));
        Check("cloud-side edit of an unchanged note is kept (no ping-pong)", plan.KeptCloudVersion == 1);
        var deletes = plan.Deletes.OrderBy(x => x).ToList();
        Check("deletes = ours gone locally + unticked folder", deletes.SequenceEqual(new[] { "P/old.md", "Q/deselected.md" }),
              string.Join(",", deletes));
        Check("another machine's note is never deleted", !plan.Deletes.Contains("Other/theirs.md") && !plan.Forget.Contains("Other/theirs.md"));
        Check("a missing selected folder deletes nothing", !plan.Deletes.Contains("M/missing.md") && plan.HeldForMissingFolder == 1);
        Check("a case-only rename is forgotten, not deleted", plan.Forget.Contains("P/Case.md") && !plan.Deletes.Contains("P/Case.md"));
        Check("case-only rename adopts the new casing", plan.Adopt.ContainsKey("P/case.md"));

        var noDel = CloudSyncEngine.PlanPush(scan, new[] { "P", "M" }, manifest, state, allowDeletes: false, allowMassDelete: false);
        Check("allowDeletes=false never deletes", noDel.Deletes.Count == 0);

        // Mass-deletion guard: 30 of 40 notes vanish from a still-selected folder.
        var big = new CloudSyncState { AccountId = "acc" };
        var bigScan = new LocalScan();
        var files = new List<(string, string)>();
        for (var i = 0; i < 40; i++)
        {
            var p = $"B/n{i:00}.md";
            big.Uploaded[p] = H("n" + i);
            files.Add((p, "n" + i));
            if (i < 10) bigScan.Notes.Add(N(p, "n" + i));
        }
        var bigManifest = M(files.ToArray());
        var held = CloudSyncEngine.PlanPush(bigScan, new[] { "B" }, bigManifest, big, true, false);
        Check("30 of 40 vanishing is held back", held.Deletes.Count == 0 && held.HeldBack.Count == 30);
        var allowed = CloudSyncEngine.PlanPush(bigScan, new[] { "B" }, bigManifest, big, true, true);
        Check("…and sent once allowed", allowed.Deletes.Count == 30);
        var unticked = CloudSyncEngine.PlanPush(new LocalScan(), Array.Empty<string>(), bigManifest, big, true, false);
        Check("unticking a whole folder is not held back (the owner already answered)", unticked.Deletes.Count == 40);
    }

    private static void PullPlanning()
    {
        var state = new CloudSyncState { AccountId = "acc" };
        state.Uploaded["A/clean.md"] = H("c1");
        state.Uploaded["A/dirty.md"] = H("d1");
        state.Uploaded["A/gone.md"] = H("g");
        state.Uploaded["A/gone-dirty.md"] = H("gd1");
        state.Uploaded["A/same.md"] = H("s");

        var scan = new LocalScan();
        scan.Notes.Add(N("A/clean.md", "c1"));          // cloud has c2          → fetch
        scan.Notes.Add(N("A/dirty.md", "d2"));          // cloud d3, ours d2     → keep ours
        scan.Notes.Add(N("A/gone.md", "g"));            // cloud deleted it      → delete here
        scan.Notes.Add(N("A/gone-dirty.md", "gd2"));    // cloud deleted, ours changed → keep
        scan.Notes.Add(N("A/local-new.md", "ln"));      // written here, unpushed → keep
        scan.Notes.Add(N("A/same.md", "s"));

        var manifest = M(("A/clean.md", "c2"), ("A/dirty.md", "d3"), ("A/same.md", "s"), ("A/new.md", "n"),
                         ("../evil.md", "x"), ("CLAUDE.md", "server export"), ("A/Same.md", "case twin"));
        var plan = CloudSyncEngine.PlanPull(scan, manifest, state);
        var fetch = plan.Fetch.Select(f => f.Path).OrderBy(x => x).ToList();
        Check("fetch = new in cloud + changed in cloud while clean here", fetch.SequenceEqual(new[] { "A/clean.md", "A/new.md" }),
              string.Join(",", fetch));
        Check("delete here only what the cloud removed and we never touched",
              plan.DeleteLocal.Select(n => n.Path).SequenceEqual(new[] { "A/gone.md" }));
        Check("unpushed local changes are kept (3)", plan.KeptLocal == 3, plan.KeptLocal.ToString());
        Check("identical note is recorded in sync", plan.InSync.ContainsKey("A/same.md"));
        Check("a cloud path escaping the cache is refused", plan.Skipped.Any(s => s.Path == "../evil.md")
                                                           && !plan.Fetch.Any(f => f.Path == "../evil.md"));
        Check("a cloud CLAUDE.md is ignored", !plan.Fetch.Any(f => f.Path == "CLAUDE.md"));
        Check("a case twin is skipped, not flip-flopped", plan.Skipped.Any(s => s.Path == "A/Same.md"));

        var many = new CloudSyncState { AccountId = "acc" };
        var manyScan = new LocalScan();
        for (var i = 0; i < 30; i++) { many.Uploaded[$"Z/{i}.md"] = H("z" + i); manyScan.Notes.Add(N($"Z/{i}.md", "z" + i)); }
        var emptied = CloudSyncEngine.PlanPull(manyScan, new CloudManifest(), many);
        Check("an empty manifest does not empty the cache", emptied.DeleteLocal.Count == 0 && emptied.HeldBack.Count == 30);
    }

    private static void CredentialStore(string root)
    {
        var store = new CloudCredentialStore(Path.Combine(root, "store-unit", "cloud.json"));
        const string key = "abcd-efgh-1234-wxyz";
        const string token = "bxc_UNIT_TEST_TOKEN_should_never_be_in_clear";
        var rec = store.SaveLogin(new CloudLoginResult
        {
            Token = token, TokenId = "t1",
            Account = new CloudAccount { Id = "acc1", LicenseType = "monthly", IsValid = true },
        }, "UNIT-PC", key);
        var text = File.ReadAllText(store.FilePath);
        Check("license key is not stored", !text.Contains(key, StringComparison.OrdinalIgnoreCase)
                                             && !text.Contains("EFGH-1234", StringComparison.OrdinalIgnoreCase));
        Check("only the last 4 of the key are kept", rec.LicenseKeyHint == "WXYZ" && text.Contains("\"WXYZ\""));
        if (OperatingSystem.IsWindows())
            Check("token is DPAPI-wrapped on disk (not in clear)", !text.Contains(token) && text.Contains("\"dpapi\""));
        var back = store.Load();
        Check("token round-trips through the store", back?.Token == token && back.AccountId == "acc1");
        store.Clear();
        Check("sign-out removes the file", store.Load() == null && !File.Exists(store.FilePath));

        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("brainx-cloud-v1|" + key.Trim().ToUpperInvariant()))).ToLowerInvariant()[..32];
        Check("account id follows the contract formula", CloudCredentialStore.AccountIdForKey("  " + key + " ") == expected);
    }

    // ── 2. end to end ────────────────────────────────────────────────

    private static async Task EndToEndAsync(string root)
    {
        using var fake = new CloudFakeServer();
        fake.ValidKeys.Add(Key);
        fake.ValidKeys.Add(Key2);
        fake.ExpiredKeys.Add(ExpiredKey);
        using var api = new CloudApiClient(null, fake.BaseUrl, timeout: TimeSpan.FromSeconds(10));

        await ExpectCode("wrong key → INVALID_LICENSE", CloudErrorCodes.InvalidLicense, () => api.LoginAsync("NOPE-NOPE", "pc"));
        await ExpectCode("empty key → INVALID_LICENSE (no request)", CloudErrorCodes.InvalidLicense, () => api.LoginAsync("   ", "pc"));
        await ExpectCode("expired key → LICENSE_EXPIRED", CloudErrorCodes.LicenseExpired, () => api.LoginAsync(ExpiredKey, "pc"));
        await ExpectCode("no token → NOT_SIGNED_IN", CloudErrorCodes.NotSignedIn, () => api.GetAccountAsync());

        var login = await api.LoginAsync(Key, "selftest-pc");
        api.SetToken(login.Token);
        var acct = login.Account.Id;
        Check("login returns a bxc_ token and the contract's account id",
              login.Token.StartsWith("bxc_") && acct == CloudCredentialStore.AccountIdForKey(Key));

        // ── a vault ──
        var vault = Path.Combine(root, "vault");
        void Write(string rel, string content)
        {
            var f = Path.Combine(vault, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, content, new UTF8Encoding(false));
        }
        Write("Programming/a.md", "# A\nline\n");
        Write("Programming/crlf.md", "# CRLF\r\nline\r\n");
        Write("Programming/โน้ตภาษาไทย.md", "# ไทย\r\nเนื้อหา ✓\n");
        Write("Programming/Sub/deep.md", "deep");
        Write("Programming/big.md", new string('x', 2 * 1024 * 1024 + 10));
        Write("Notes/c.md", "not selected");
        Write("root-note.md", "root");
        Write("CLAUDE.md", "machine managed");
        Write(".obsidianx/hidden.md", "never");
        Write("Programming/.hidden/x.md", "never");

        var engine = new CloudSyncEngine(api);
        var state = CloudSyncState.Load(vault);
        state.Folders = new List<string> { "Programming", CloudSyncState.RootToken };
        state.Save(vault);
        var opts = new CloudPushOptions { AccountId = acct };

        var r1 = await engine.PushAsync(vault, state.Folders, state, opts);
        var cloud = fake.NotesOf(acct);
        Check("first push uploads the 5 eligible notes", r1.Ok && r1.Uploaded == 5, $"{r1.ErrorCode} uploaded={r1.Uploaded}");
        Check("cloud holds exactly the chosen notes",
              cloud.Keys.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[]
              {
                  "Programming/Sub/deep.md", "Programming/a.md", "Programming/crlf.md", "Programming/โน้ตภาษาไทย.md", "root-note.md",
              }.OrderBy(x => x, StringComparer.Ordinal)), string.Join(",", cloud.Keys));
        Check("CRLF survives the upload byte for byte", cloud.GetValueOrDefault("Programming/crlf.md") == "# CRLF\r\nline\r\n");
        Check("Thai file name and text survive", cloud.GetValueOrDefault("Programming/โน้ตภาษาไทย.md") == "# ไทย\r\nเนื้อหา ✓\n");
        Check("a note over 2 MB is skipped with a reason", r1.Skipped.Any(s => s.Path == "Programming/big.md"));
        Check("CLAUDE.md, dot-folders and unticked folders stay home",
              !cloud.ContainsKey("CLAUDE.md") && !cloud.Keys.Any(k => k.Contains(".obsidianx") || k.Contains(".hidden") || k.StartsWith("Notes/")));
        Check("state saved with the 5 uploads", CloudSyncState.Load(vault).Uploaded.Count == 5);

        var r2 = await engine.PushAsync(vault, state.Folders, state, opts);
        Check("second push sends nothing (no ping-pong)", r2.Ok && r2.Uploaded == 0 && r2.Unchanged == 5, $"up={r2.Uploaded} same={r2.Unchanged}");

        File.AppendAllText(Path.Combine(vault, "Programming", "a.md"), "edited\n");
        var r3 = await engine.PushAsync(vault, state.Folders, state, opts);
        Check("an edit uploads exactly one note", r3.Uploaded == 1);

        // Another machine puts a note in the same folder.
        using (var other = new CloudApiClient(null, fake.BaseUrl))
        {
            var l2 = await other.LoginAsync(Key, "other-pc");
            other.SetToken(l2.Token);
            await other.UploadNotesAsync(new List<CloudNoteContent>
            {
                new() { Path = "Programming/other.md", Content = "from another machine", Sha256 = H("from another machine") },
            });
        }
        var r4 = await engine.PushAsync(vault, state.Folders, state, opts);
        Check("another machine's note is left alone", r4.Ok && r4.Deleted == 0 && fake.NotesOf(acct).ContainsKey("Programming/other.md")
                                                      && !state.Uploaded.ContainsKey("Programming/other.md"));
        Check("…and is not pulled into the vault", !File.Exists(Path.Combine(vault, "Programming", "other.md")));

        File.Delete(Path.Combine(vault, "Programming", "crlf.md"));
        var r5 = await engine.PushAsync(vault, state.Folders, state, opts);
        Check("a note deleted here is deleted from the cloud", r5.Deleted == 1 && !fake.NotesOf(acct).ContainsKey("Programming/crlf.md"));

        // Untick "Programming", answer "keep it in the cloud".
        foreach (var p in state.UploadedUnder("Programming")) state.Uploaded.Remove(p);
        state.Folders = new List<string> { CloudSyncState.RootToken };
        state.Save(vault);
        var r6 = await engine.PushAsync(vault, state.Folders, state, opts);
        Check("untick + keep: nothing deleted", r6.Ok && r6.Deleted == 0 && fake.NotesOf(acct).ContainsKey("Programming/a.md"));

        // Tick it again (adopts what is there), then untick + remove.
        state.Folders = new List<string> { "Programming", CloudSyncState.RootToken };
        var r7 = await engine.PushAsync(vault, state.Folders, state, opts);
        Check("re-tick adopts identical notes without re-sending", r7.Ok && r7.Uploaded == 0, $"uploaded={r7.Uploaded}");
        state.Folders = new List<string> { CloudSyncState.RootToken };
        var r8 = await engine.PushAsync(vault, state.Folders, state, opts);
        var after8 = fake.NotesOf(acct);
        Check("untick + remove deletes only this vault's notes of that folder",
              r8.Deleted == 3 && !after8.ContainsKey("Programming/a.md") && after8.ContainsKey("Programming/other.md"),
              $"deleted={r8.Deleted}");
        state.Folders = new List<string> { "Programming", CloudSyncState.RootToken };
        await engine.PushAsync(vault, state.Folders, state, opts);

        // Batching: 450 small notes.
        for (var i = 0; i < 450; i++) Write($"Bulk/n{i:000}.md", $"# note {i}\r\n" + new string('b', 1500));
        state.Folders.Add("Bulk");
        var up0 = fake.UploadRequests;
        var r9 = await engine.PushAsync(vault, state.Folders, state, opts);
        Check("450 notes go up in ≥3 requests of ≤200", r9.Uploaded == 450 && fake.UploadRequests - up0 >= 3 && fake.MaxFilesPerUpload <= 200,
              $"uploaded={r9.Uploaded} requests={fake.UploadRequests - up0} max={fake.MaxFilesPerUpload}");

        // Size batching: 1.5 MB notes, CRLF-heavy, must stay under the 8 MB body.
        for (var i = 0; i < 6; i++) Write($"Heavy/h{i}.md", string.Concat(Enumerable.Repeat("line\r\n", 250_000)));
        state.Folders.Add("Heavy");
        var r10 = await engine.PushAsync(vault, state.Folders, state, opts);
        Check("CRLF-heavy 1.5 MB notes batch under the 8 MB body limit", r10.Uploaded == 6 && fake.MaxUploadBody <= 8 * 1024 * 1024,
              $"uploaded={r10.Uploaded} maxBody={fake.MaxUploadBody} {r10.ErrorCode}");

        // Cancel mid-sync, then resume.
        for (var i = 0; i < 600; i++) Write($"Many/m{i:000}.md", $"many {i}");
        state.Folders.Add("Many");
        using (var cts = new CancellationTokenSource())
        {
            var start = fake.UploadRequests;
            fake.OnUpload = n => { if (n == start + 2) cts.Cancel(); };
            var rc = await engine.PushAsync(vault, state.Folders, state, opts, null, cts.Token);
            fake.OnUpload = null;
            var saved = CloudSyncState.Load(vault).Uploaded.Keys.Count(k => k.StartsWith("Many/"));
            Check("cancel stops the sync and keeps finished batches", rc.Cancelled && saved >= 200 && saved < 600,
                  $"cancelled={rc.Cancelled} saved={saved}");
            var rr = await engine.PushAsync(vault, state.Folders, state, opts);
            var inCloud = fake.NotesOf(acct).Keys.Count(k => k.StartsWith("Many/"));
            Check("resume finishes without re-sending the finished batches",
                  rr.Ok && inCloud == 600 && rr.Uploaded <= 600 - saved, $"uploaded={rr.Uploaded} saved={saved} cloud={inCloud}");
        }

        // A name only the server refuses: isolated, the rest still goes.
        fake.RejectPathContaining = "reject-me";
        Write("Programming/reject-me.md", "server says no");
        Write("Programming/ok-too.md", "fine");
        var r11 = await engine.PushAsync(vault, state.Folders, state, opts);
        fake.RejectPathContaining = null;
        Check("a server-refused note is skipped, its batch-mates still upload",
              r11.Ok && r11.Skipped.Any(s => s.Path == "Programming/reject-me.md") && fake.NotesOf(acct).ContainsKey("Programming/ok-too.md"),
              $"{r11.ErrorCode} uploaded={r11.Uploaded}");

        // Mass deletion: 300 of 450 Bulk notes vanish.
        for (var i = 0; i < 300; i++) File.Delete(Path.Combine(vault, "Bulk", $"n{i:000}.md"));
        var r12 = await engine.PushAsync(vault, state.Folders, state, opts);
        Check("300 of 450 vanishing is held back, cloud untouched", r12.HeldBackDeletes == 300 && r12.Deleted == 0
                                                                     && fake.NotesOf(acct).Keys.Count(k => k.StartsWith("Bulk/")) == 450,
              $"held={r12.HeldBackDeletes} deleted={r12.Deleted}");
        var r13 = await engine.PushAsync(vault, state.Folders, state, new CloudPushOptions { AccountId = acct, AllowMassDelete = true });
        Check("…and deleted once confirmed", r13.Deleted == 300);

        // Quota.
        var q = fake.QuotaBytes;
        fake.QuotaBytes = fake.NotesOf(acct).Values.Sum(v => (long)Encoding.UTF8.GetByteCount(v)) + 1000;
        Write("Programming/over-quota.md", new string('q', 50_000));
        var r14 = await engine.PushAsync(vault, state.Folders, state, opts);
        fake.QuotaBytes = q;
        Check("over quota → QUOTA_EXCEEDED, nothing lost", r14.ErrorCode == CloudErrorCodes.QuotaExceeded
                                                            && !state.Uploaded.ContainsKey("Programming/over-quota.md"));
        var r15 = await engine.PushAsync(vault, state.Folders, state, opts);
        Check("…and it goes up once there is room", r15.Ok && state.Uploaded.ContainsKey("Programming/over-quota.md"));

        // Offline.
        var deadPort = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        deadPort.Start();
        var port = ((System.Net.IPEndPoint)deadPort.LocalEndpoint).Port;
        deadPort.Stop();
        using (var offline = new CloudApiClient(login.Token, $"http://127.0.0.1:{port}", timeout: TimeSpan.FromSeconds(5)))
        {
            var before = CloudSyncState.Load(vault).Uploaded.Count;
            File.AppendAllText(Path.Combine(vault, "root-note.md"), "\noffline edit");
            var r16 = await new CloudSyncEngine(offline).PushAsync(vault, state.Folders, CloudSyncState.Load(vault), opts);
            Check("offline → NETWORK error, state intact", r16.ErrorCode == CloudErrorCodes.Network
                                                          && CloudSyncState.Load(vault).Uploaded.Count == before, r16.ErrorCode);
        }

        // Timeout (the server answers too slowly).
        fake.Delay = TimeSpan.FromSeconds(3);
        using (var slow = new CloudApiClient(login.Token, fake.BaseUrl, timeout: TimeSpan.FromSeconds(1)))
            await ExpectCode("slow server → TIMEOUT (not a cancel)", CloudErrorCodes.Timeout, () => slow.GetAccountAsync());
        fake.Delay = TimeSpan.Zero;

        // One sync per root.
        using (new FileStream(Path.Combine(vault, ".obsidianx", "cloud-sync.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var busy = await engine.PushAsync(vault, state.Folders, state,
                new CloudPushOptions { AccountId = acct, LockTimeout = TimeSpan.FromMilliseconds(300) });
            Check("a second sync of the same vault reports BUSY", busy.ErrorCode == CloudSyncResult.Busy);
        }
        var missing = await engine.PushAsync(Path.Combine(root, "no-such-vault"), state.Folders, new CloudSyncState(), opts);
        Check("a missing vault is reported, nothing created", missing.ErrorCode == CloudSyncResult.VaultMissing
                                                              && !Directory.Exists(Path.Combine(root, "no-such-vault")));
        await engine.PushAsync(vault, state.Folders, state, opts);

        // ── the cache (pull) ──
        var cache = CloudCredentialStore.CacheDirFor(acct);
        var p1 = await engine.PullAsync(cache, new CloudPullOptions { AccountId = acct });
        var serverNow = fake.NotesOf(acct);
        Check("pull fetches every cloud note", p1.Ok && p1.Fetched == serverNow.Count, $"{p1.ErrorCode} fetched={p1.Fetched} cloud={serverNow.Count}");
        var thaiLocal = Path.Combine(cache, "Programming", "โน้ตภาษาไทย.md");
        Check("pulled Thai note reads back exactly", File.Exists(thaiLocal) && CloudSyncEngine.ReadText(thaiLocal) == "# ไทย\r\nเนื้อหา ✓\n");
        Check("another machine's note is in the cache", File.Exists(Path.Combine(cache, "Programming", "other.md")));
        var p2 = await engine.PullAsync(cache, new CloudPullOptions { AccountId = acct });
        Check("second pull fetches nothing", p2.Ok && p2.Fetched == 0 && p2.Unchanged == serverNow.Count);

        var cstate = CloudSyncState.Load(cache);
        var clean = await engine.PushAsync(cache, null, cstate, new CloudPushOptions { AccountId = acct, AllowDeletes = false, SkipIfClean = true });
        Check("clean cache push needs no network", clean.NothingToDo);
        var manifestsBefore = fake.ManifestRequests;
        var full = await engine.PushAsync(cache, null, cstate, new CloudPushOptions { AccountId = acct, AllowDeletes = false });
        Check("full cache push re-sends nothing (pull and push agree on every hash)", full.Ok && full.Uploaded == 0
                                                                                        && fake.ManifestRequests == manifestsBefore + 1,
              $"uploaded={full.Uploaded}");

        // A tool writes into the cache → pushed.
        var toolNote = Path.Combine(cache, "Programming", "a.md");
        File.AppendAllText(toolNote, "appended by a tool ✓\n", new UTF8Encoding(false));
        var tp = await engine.PushAsync(cache, null, CloudSyncState.Load(cache),
            new CloudPushOptions { AccountId = acct, AllowDeletes = false, SkipIfClean = true });
        Check("a write in the cache is pushed", tp.Uploaded == 1 && fake.NotesOf(acct)["Programming/a.md"].EndsWith("appended by a tool ✓\n"));
        var p3 = await engine.PullAsync(cache, new CloudPullOptions { AccountId = acct });
        Check("…and the next pull has nothing to do", p3.Fetched == 0 && p3.DeletedLocal == 0);

        // Changes made in the cloud.
        fake.PutNote(acct, "Programming/ok-too.md", "changed in the cloud");
        fake.RemoveNote(acct, "Programming/Sub/deep.md");
        var p4 = await engine.PullAsync(cache, new CloudPullOptions { AccountId = acct });
        Check("pull takes a cloud edit", p4.Fetched == 1 && File.ReadAllText(Path.Combine(cache, "Programming", "ok-too.md")) == "changed in the cloud");
        Check("pull removes a note the cloud deleted (and its empty folder)",
              p4.DeletedLocal == 1 && !File.Exists(Path.Combine(cache, "Programming", "Sub", "deep.md"))
              && !Directory.Exists(Path.Combine(cache, "Programming", "Sub")));

        // A local edit not yet pushed while the cloud deletes the same note.
        File.AppendAllText(Path.Combine(cache, "root-note.md"), "\nlocal edit, not pushed");
        fake.RemoveNote(acct, "root-note.md");
        var p5 = await engine.PullAsync(cache, new CloudPullOptions { AccountId = acct });
        Check("an unpushed local edit survives a cloud deletion", File.Exists(Path.Combine(cache, "root-note.md")) && p5.KeptLocal >= 1);

        fake.PutNote(acct, "../escape.md", "evil");
        var p6 = await engine.PullAsync(cache, new CloudPullOptions { AccountId = acct });
        Check("a cloud path outside the cache is never written", !File.Exists(Path.Combine(Path.GetDirectoryName(cache)!, "escape.md"))
                                                                && p6.Skipped.Any(s => s.Path == "../escape.md"));
        fake.RemoveNote(acct, "../escape.md");

        // ── tokens ──
        var created = await api.CreateTokenAsync("laptop", CloudScopes.Read);
        var tokens = await api.ListTokensAsync();
        Check("an api token is created and listed", created.Token.StartsWith("bxc_") && tokens.Any(t => t.Name == "laptop" && t.Kind == "api"));
        using (var readOnly = new CloudApiClient(created.Token, fake.BaseUrl))
        {
            await ExpectCode("an api token cannot manage tokens", CloudErrorCodes.Forbidden, () => readOnly.ListTokensAsync());
            await ExpectCode("a read token cannot upload", CloudErrorCodes.Forbidden, () => readOnly.UploadNotesAsync(
                new List<CloudNoteContent> { new() { Path = "x.md", Content = "x", Sha256 = H("x") } }));
            await api.RevokeTokenAsync(created.Id);
            try { await readOnly.GetAccountAsync(); Fail("a revoked token stops working", "still accepted"); }
            catch (CloudApiException ex) { Check("a revoked token stops working (auth failure)", ex.IsAuthFailure, ex.Code); }
        }
        var cmd = CloudEndpoints.ClaudeMcpAddCommand(CloudEndpoints.DefaultBaseUrl, "bxc_example");
        Check("the connect command is the contract's", cmd == "claude mcp add --transport http brainx https://serverbrain.xman4289.com/mcp --header \"Authorization: Bearer bxc_example\"", cmd);

        // ── an expired license: writes refused, reads still allowed ──
        using (var api2 = new CloudApiClient(null, fake.BaseUrl))
        {
            var l2 = await api2.LoginAsync(Key2, "pc2");
            api2.SetToken(l2.Token);
            fake.ExpireAccount(l2.Account.Id);
            var acc2 = await api2.GetAccountAsync();
            Check("expired account still answers /account (isValid=false)", !acc2.IsValid);
            var v2 = Path.Combine(root, "vault2");
            Directory.CreateDirectory(Path.Combine(v2, "N"));
            File.WriteAllText(Path.Combine(v2, "N", "x.md"), "x");
            var s2 = new CloudSyncState { Folders = { "N" } };
            var r2x = await new CloudSyncEngine(api2).PushAsync(v2, s2.Folders, s2, new CloudPushOptions { AccountId = l2.Account.Id });
            Check("expired license → LICENSE_EXPIRED on push", r2x.ErrorCode == CloudErrorCodes.LicenseExpired, r2x.ErrorCode);
            var m2 = await api2.GetManifestAsync();
            Check("…while the manifest can still be read", m2.Files.Count == 0);
        }

        // ── sign out ──
        await api.LogoutAsync();
        await ExpectCode("after logout the device token is dead", CloudErrorCodes.Unauthorized, () => api.GetAccountAsync());

        Check("results never carry the token", !r1.ErrorMessage?.Contains(login.Token) ?? true);
    }

    // ── 2b. the CLI, in-process ──────────────────────────────────────

    private static async Task<(int Code, string Out)> Cli(params string[] args)
    {
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetError(sw);
        try { return (await Program.CloudCliAsync(args), sw.ToString()); }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    private static async Task CliAsync(string root)
    {
        using var fake = new CloudFakeServer();
        fake.ValidKeys.Add(Key);
        Environment.SetEnvironmentVariable("BRAINX_CLOUD_URL", fake.BaseUrl);
        try
        {
            var store = new CloudCredentialStore();
            store.Clear();

            var (bad, badOut) = await Cli("login", "NOT-A-KEY", "--device", "cli-test");
            Check("cloud login with a wrong key fails with a readable message", bad != 0 && badOut.Contains("not valid"), badOut.Trim());

            var (ok, okOut) = await Cli("login", Key, "--device", "cli-test");
            var rec = store.Load();
            Check("cloud login signs in", ok == 0 && rec?.IsSignedIn == true && rec.DeviceName == "cli-test", okOut.Trim());
            Check("cloud login prints neither the key nor the token",
                  !okOut.Contains(Key, StringComparison.OrdinalIgnoreCase) && (rec?.Token == null || !okOut.Contains(rec.Token)));

            var (st, stOut) = await Cli("status");
            Check("cloud status shows the account", st == 0 && stOut.Contains("license:") && stOut.Contains("storage:"), stOut.Trim());

            var vault = Path.Combine(root, "cli-vault");
            Directory.CreateDirectory(Path.Combine(vault, "Ideas"));
            File.WriteAllText(Path.Combine(vault, "Ideas", "one.md"), "# one\r\n");
            File.WriteAllText(Path.Combine(vault, "top.md"), "top");
            var (push, pushOut) = await Cli("push", "--vault", vault, "--folders", "Ideas,/");
            var acct = rec!.AccountId!;
            Check("cloud push uploads the chosen folders", push == 0 && fake.NotesOf(acct).Count == 2, pushOut.Trim());

            var (drop, dropOut) = await Cli("push", "--vault", vault, "--folders", "/");
            Check("dropping a folder without --remove-deselected keeps its notes in the cloud",
                  drop == 0 && fake.NotesOf(acct).ContainsKey("Ideas/one.md") && dropOut.Contains("stay in the cloud"), dropOut.Trim());

            var (pull, pullOut) = await Cli("pull");
            var cache = CloudCredentialStore.CacheDirFor(acct);
            Check("cloud pull fills and indexes the cache",
                  pull == 0 && File.Exists(Path.Combine(cache, "Ideas", "one.md"))
                  && File.Exists(Path.Combine(cache, ".obsidianx", "brain-export.json")), pullOut.Trim());

            var (tc, tcOut) = await Cli("token", "create", "laptop", "--read");
            Check("cloud token create prints the connect command once", tc == 0 && tcOut.Contains("claude mcp add --transport http brainx"), tcOut.Trim());
            var (tl, tlOut) = await Cli("token", "list");
            Check("cloud token list marks this machine", tl == 0 && tlOut.Contains("laptop") && tlOut.Contains("this machine"), tlOut.Trim());

            var (lo, loOut) = await Cli("logout");
            Check("cloud logout revokes and forgets", lo == 0 && store.Load() == null && loOut.Contains("revoked"), loOut.Trim());
            var (st2, _) = await Cli("status");
            Check("cloud status after logout says not signed in", st2 == 1);
            var (pl2, pl2Out) = await Cli("pull");
            Check("cloud pull when signed out explains how to sign in", pl2 != 0 && pl2Out.Contains("cloud login"), pl2Out.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BRAINX_CLOUD_URL", null);
        }
    }

    // ── 3. serve mode ────────────────────────────────────────────────

    private static async Task ServeModeAsync(string root)
    {
        using var fake = new CloudFakeServer();
        fake.ValidKeys.Add(Key);
        CloudLoginResult login;
        using (var api = new CloudApiClient(null, fake.BaseUrl))
        {
            login = await api.LoginAsync(Key, "serve-test");
            api.SetToken(login.Token);
            await api.UploadNotesAsync(new List<CloudNoteContent>
            {
                new() { Path = "Knowledge/cloud-first.md", Content = "# Cloud first\r\nThis note lives in the cloud. ข้อความภาษาไทย\r\n", Sha256 = H("# Cloud first\r\nThis note lives in the cloud. ข้อความภาษาไทย\r\n") },
                new() { Path = "Knowledge/second.md", Content = "second note", Sha256 = H("second note") },
            });
        }
        new CloudCredentialStore().SaveLogin(login, "serve-test", Key);
        var acct = login.Account.Id;
        var cache = CloudCredentialStore.CacheDirFor(acct);
        if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);

        // Online start: pulls, indexes, serves; a write goes back to the cloud.
        await using (var mcp = await McpChild.StartAsync(fake.BaseUrl))
        {
            var init = await mcp.RequestAsync("initialize", new JObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "cloud-selftest", ["version"] = "1" },
            }, TimeSpan.FromSeconds(60));
            Check("serve mode answers initialize", init?["result"]?["serverInfo"] != null, init?.ToString(Formatting.None));
            Check("instructions say this is the cloud brain", init?["result"]?["instructions"]?.ToString().Contains("BRAINX CLOUD") == true);
            await mcp.NotifyAsync("notifications/initialized");
            Check("startup pull filled the cache", File.Exists(Path.Combine(cache, "Knowledge", "cloud-first.md")));

            var stats = Json(await mcp.CallToolAsync("brain_stats", new JObject()));
            Check("brain_stats reports cloud mode, online",
                  stats?["cloud"]?["mode"]?.ToString() == "cloud" && stats["cloud"]?["online"]?.Value<bool>() == true,
                  Trim(stats?["cloud"]?.ToString(Formatting.None) ?? "no stats"));
            Check("the cloud notes are indexed", stats?["totalNotes"]?.Value<int>() >= 2, stats?["totalNotes"]?.ToString());

            var created = await mcp.CallToolAsync("brain_create_note", new JObject
            {
                ["title"] = "Cloud selftest บันทึก",
                ["content"] = "written through the cloud cache",
                ["folder"] = "Notes",
            });
            Check("brain_create_note succeeds in cloud mode", !created.StartsWith("ERROR"), Trim(created));
            var arrived = await WaitUntil(() => fake.NotesOf(acct).Keys.Any(k => k.StartsWith("Notes/") && k.Contains("Cloud selftest")),
                                          TimeSpan.FromSeconds(20));
            Check("the new note reaches the cloud without being asked", arrived,
                  string.Join(",", fake.NotesOf(acct).Keys));
            var content = fake.NotesOf(acct).FirstOrDefault(kv => kv.Key.Contains("Cloud selftest")).Value ?? "";
            Check("…with its content", content.Contains("written through the cloud cache"));

            // Written and hung up at once (inside the push debounce): the
            // shutdown flush must still get it to the cloud.
            var last = await mcp.CallToolAsync("brain_create_note", new JObject
            {
                ["title"] = "Written right before hang-up",
                ["content"] = "the last push on exit carries this",
                ["folder"] = "Notes",
            });
            Check("a second note is written", !last.StartsWith("ERROR"), Trim(last));
            await mcp.CloseAsync();
            Check("a note written just before hang-up still reaches the cloud",
                  fake.NotesOf(acct).Keys.Any(k => k.Contains("Written right before hang-up")),
                  string.Join(",", fake.NotesOf(acct).Keys));
            Check("the server exits when the client hangs up", mcp.Exited);
            Check("the log never contains the token", !mcp.Stderr.Contains(login.Token), "token found in stderr");
            Check("the log never contains the license key", !mcp.Stderr.Contains(Key, StringComparison.OrdinalIgnoreCase));
        }

        // Offline start: the cache from above is served, and the log says so.
        var deadPort = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        deadPort.Start();
        var port = ((System.Net.IPEndPoint)deadPort.LocalEndpoint).Port;
        deadPort.Stop();
        var sw = Stopwatch.StartNew();
        await using (var mcp = await McpChild.StartAsync($"http://127.0.0.1:{port}"))
        {
            var init = await mcp.RequestAsync("initialize", new JObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JObject(),
                ["clientInfo"] = new JObject { ["name"] = "cloud-selftest", ["version"] = "1" },
            }, TimeSpan.FromSeconds(60));
            Check("offline start still answers initialize", init?["result"] != null);
            Check("…quickly (not waiting out a timeout)", sw.Elapsed < TimeSpan.FromSeconds(25), $"{sw.Elapsed.TotalSeconds:0.0}s");
            await mcp.NotifyAsync("notifications/initialized");
            var stats = Json(await mcp.CallToolAsync("brain_stats", new JObject()));
            Check("offline: brain_stats says offline",
                  stats?["cloud"]?["online"]?.Value<bool>() == false,
                  Trim(stats?["cloud"]?.ToString(Formatting.None) ?? "no stats"));
            Check("offline: the cache (incl. the notes written last session) is served",
                  stats?["totalNotes"]?.Value<int>() >= 4, stats?["totalNotes"]?.ToString());
            var search = await mcp.CallToolAsync("brain_search", new JObject { ["query"] = "cloud first" });
            Check("offline: search still answers from the cache",
                  search.Contains("cloud-first", StringComparison.OrdinalIgnoreCase)
                  || search.Contains("Cloud first", StringComparison.OrdinalIgnoreCase), Trim(search));
            await mcp.CloseAsync();
            Check("offline: the log says it is serving the cache", mcp.Stderr.Contains("offline", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;

    private static JObject? Json(string text)
    {
        try { return JObject.Parse(text); } catch { return null; }
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, TimeSpan max)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < max)
        {
            if (cond()) return true;
            await Task.Delay(250);
        }
        return cond();
    }

    /// <summary>A `brainx-mcp --serve --cloud` child spoken to over stdio.</summary>
    private sealed class McpChild : IAsyncDisposable
    {
        private readonly Process _p;
        private readonly StringBuilder _stderr = new();
        private int _id;

        public string Stderr { get { lock (_stderr) return _stderr.ToString(); } }
        public bool Exited => _p.HasExited;

        private McpChild(Process p)
        {
            _p = p;
            _p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_stderr) _stderr.AppendLine(e.Data); };
            _p.BeginErrorReadLine();
        }

        public static Task<McpChild> StartAsync(string baseUrl)
        {
            var host = Environment.ProcessPath ?? "brainx-mcp";
            var psi = new ProcessStartInfo
            {
                FileName = host,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };
            if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "brainx-mcp.dll"));
            psi.ArgumentList.Add("--serve");
            psi.ArgumentList.Add("--cloud");
            psi.Environment["BRAINX_CLOUD_URL"] = baseUrl;
            psi.Environment["BRAINX_CLOUD_STARTUP_WAIT_SEC"] = "20";
            psi.Environment["BRAINX_SANDBOX"] = "1";
            psi.Environment["BRAINX_HEADLESS"] = "1";
            psi.Environment["BRAINX_MCP_LAUNCHER_CHILD"] = "1";
            psi.Environment.Remove("BRAINX_VAULT");
            psi.Environment.Remove("BRAINX_CLOUD_TOKEN");
            var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start brainx-mcp");
            return Task.FromResult(new McpChild(p));
        }

        public async Task<JObject?> RequestAsync(string method, JObject parameters, TimeSpan timeout)
        {
            var id = ++_id;
            var msg = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };
            await _p.StandardInput.WriteLineAsync(msg.ToString(Formatting.None));
            await _p.StandardInput.FlushAsync();
            using var cts = new CancellationTokenSource(timeout);
            while (!cts.IsCancellationRequested)
            {
                string? line;
                try { line = await _p.StandardOutput.ReadLineAsync(cts.Token); }
                catch (OperationCanceledException) { return null; }
                if (line == null) return null;
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject o;
                try { o = JObject.Parse(line); } catch { continue; }
                if (o["id"]?.Type == JTokenType.Integer && o["id"]!.Value<int>() == id) return o;
            }
            return null;
        }

        public async Task NotifyAsync(string method)
        {
            await _p.StandardInput.WriteLineAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = method }.ToString(Formatting.None));
            await _p.StandardInput.FlushAsync();
        }

        /// <summary>The tool's first text block (its result), or "ERROR: …" when the call failed.</summary>
        public async Task<string> CallToolAsync(string name, JObject args)
        {
            var r = await RequestAsync("tools/call", new JObject { ["name"] = name, ["arguments"] = args }, TimeSpan.FromSeconds(60));
            if (r == null) return "ERROR: no answer";
            if (r["error"] != null) return "ERROR: " + r["error"]!.ToString(Formatting.None);
            var text = (r["result"]?["content"] as JArray)?.FirstOrDefault()?["text"]?.ToString() ?? "";
            return r["result"]?["isError"]?.Value<bool>() == true ? "ERROR: " + text : text;
        }

        public async Task CloseAsync()
        {
            try { _p.StandardInput.Close(); } catch { }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await _p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_p.HasExited)
            {
                await CloseAsync();
                if (!_p.HasExited) { try { _p.Kill(entireProcessTree: true); } catch { } }
            }
            _p.Dispose();
            if (_verbose)
            {
                Console.WriteLine("  ┌ child log");
                foreach (var l in Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    Console.WriteLine("  │ " + l.TrimEnd('\r'));
                Console.WriteLine("  └");
            }
        }
    }
}
