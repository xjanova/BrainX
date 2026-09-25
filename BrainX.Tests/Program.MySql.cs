using System.Collections.Concurrent;
using System.Globalization;
using BrainX.Core.Models;
using BrainX.Core.Services;
using BrainX.Server.Cloud;
using MySqlConnector;

namespace BrainX.Tests;

/// <summary>
/// The node on MySQL (BrainX:StorageProvider=mysql): its brain storage and
/// BrainX Cloud's account store, against a real MySQL 8.4.
///
/// Needs BRAINX_TEST_MYSQL = a connection string to a THROWAWAY server whose
/// user may CREATE and DROP DATABASE, e.g.
///     Server=127.0.0.1;Port=33306;User ID=root;Password=
/// Every check makes its own database and every database is dropped at the
/// end. Unset (CI) = these checks say SKIP. Set = the cloud checks also run a
/// second time with every test node's accounts in MySQL.
/// </summary>
internal static partial class Program
{
    private static class MySqlTestDb
    {
        public static readonly string? Server = Environment.GetEnvironmentVariable("BRAINX_TEST_MYSQL");
        public static bool Enabled => !string.IsNullOrWhiteSpace(Server);

        /// <summary>When true, CloudNode.StartAsync keeps each node's accounts in a fresh MySQL database.</summary>
        public static bool CloudNodesUseMySql;

        private static readonly ConcurrentBag<string> Created = [];

        public static string NewDatabase()
        {
            var name = "brainx_t_" + Guid.NewGuid().ToString("N")[..12];
            Exec(Server!, $"CREATE DATABASE {name} CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci");
            Created.Add(name);
            return new MySqlConnectionStringBuilder(Server) { Database = name }.ConnectionString;
        }

        public static void DropAll()
        {
            if (!Enabled) return;
            foreach (var name in Created) Exec(Server!, $"DROP DATABASE IF EXISTS {name}");
        }

        public static void Exec(string connString, string sql)
        {
            using var c = new MySqlConnection(connString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public static object? Scalar(string connString, string sql)
        {
            using var c = new MySqlConnection(connString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            return cmd.ExecuteScalar();
        }

        public static string ShowCreate(string connString, string table)
        {
            using var c = new MySqlConnection(connString);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SHOW CREATE TABLE {table}";
            using var r = cmd.ExecuteReader();
            return r.Read() ? r.GetString(1) : "";
        }
    }

    private static bool SkipWithoutMySql()
    {
        if (MySqlTestDb.Enabled) return false;
        Console.WriteLine("  [SKIP] BRAINX_TEST_MYSQL is not set (no throwaway MySQL to test against)");
        return true;
    }

    private static void RegisterMySqlChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("mysql brain storage: schema, ngram FULLTEXT, Thai search, clipped strings, access-log replay", MySqlBrainStorageChecks));
        checks.Add(("mysql brain storage: an older schema is upgraded in place (ngram index, access-log key, duplicates dropped)", MySqlLegacyUpgradeChecks));
        checks.Add(("mysql brain storage: share scopes — insert, update keeps created, list, delete, exact addresses, th-TH", MySqlScopeChecks));
        checks.Add(("mysql cloud store: accounts, tokens, the 20-cap and eviction, exact hashes, stats, delete, unique violations", MySqlCloudStoreChecks));
        checks.Add(("mysql cloud store: cloud.db moves into an empty MySQL once, is renamed, and is never merged into a full one", MySqlCloudMigrationChecks));
    }

    private static Task MySqlBrainStorageChecks()
    {
        if (SkipWithoutMySql()) return Task.CompletedTask;
        var cs = MySqlTestDb.NewDatabase();
        var s = new MySqlBrainStorage(cs);
        s.Initialize();
        s.Initialize();
        Check("Initialize twice is fine", true);
        Check("the nodes FULLTEXT index uses the ngram parser (Thai has no spaces)",
              MySqlTestDb.ShowCreate(cs, "nodes").Contains("ngram", StringComparison.OrdinalIgnoreCase), MySqlTestDb.ShowCreate(cs, "nodes"));
        Check("access_log has the one-row-per-event key", MySqlTestDb.ShowCreate(cs, "access_log").Contains("ux_access_event"));
        Check("share_scopes exists", MySqlTestDb.ShowCreate(cs, "share_scopes").Contains("owner_address"));

        var now = DateTime.UtcNow;
        var thai = new KnowledgeNode { Id = "th0000000001", Title = "ดวงดาวและจักรวาล คู่มือดูดาว", PrimaryCategory = KnowledgeCategory.Programming, Tags = ["ดาราศาสตร์"], CreatedAt = now, ModifiedAt = now };
        var english = new KnowledgeNode { Id = "en0000000001", Title = "Kestrel request limits", PrimaryCategory = KnowledgeCategory.Programming, Tags = ["aspnet"], CreatedAt = now, ModifiedAt = now };
        var longOne = new KnowledgeNode { Id = "lg0000000001", Title = new string('ก', 600), Tags = [.. Enumerable.Range(0, 200).Select(i => "tag" + i)], CreatedAt = now, ModifiedAt = now };
        var graph = new KnowledgeGraph
        {
            Nodes = [thai, english, longOne],
            Edges = [new KnowledgeEdge { SourceId = thai.Id, TargetId = english.Id, Strength = 0.5, RelationType = "wiki-link" }],
        };
        var threw = Record(() => s.UpsertGraph(graph));
        Check("UpsertGraph takes a 600-char title and 200 tags (clipped, not a failed transaction)", threw == null, Describe(threw));
        Check("three nodes stored", s.NodeCount() == 3, s.NodeCount().ToString());
        Check("the long title was clipped to 512 characters",
              Convert.ToInt32(MySqlTestDb.Scalar(cs, "SELECT CHAR_LENGTH(title) FROM nodes WHERE id = 'lg0000000001'")) == 512);
        var hitsThai = s.Search("ดาว");
        Check("a Thai query finds the Thai note", hitsThai.Any(h => h.NodeId == thai.Id), string.Join(",", hitsThai.Select(h => h.NodeId)));
        var hitsEnglish = s.Search("Kestrel");
        Check("an English query finds the English note", hitsEnglish.Any(h => h.NodeId == english.Id), string.Join(",", hitsEnglish.Select(h => h.NodeId)));
        s.UpsertGraph(graph);
        Check("a second UpsertGraph replaces, does not duplicate", s.NodeCount() == 3, s.NodeCount().ToString());

        var at = new DateTime(2026, 9, 25, 10, 0, 0, 123, DateTimeKind.Utc);
        s.LogAccess(thai.Id, "read", "ctx", at);
        s.LogAccess(thai.Id, "read", "ctx", at);
        s.LogAccess(thai.Id, "read", "ctx", at.AddSeconds(1));
        s.LogAccess(english.Id, "read");
        Check("a replayed event is stored once (3 distinct events → 3 rows)",
              Convert.ToInt32(MySqlTestDb.Scalar(cs, "SELECT COUNT(*) FROM access_log")) == 3,
              MySqlTestDb.Scalar(cs, "SELECT COUNT(*) FROM access_log")?.ToString());
        var top = s.TopAccessed(10, TimeSpan.FromDays(3650));
        Check("TopAccessed counts hits per node and knows the title",
              top.FirstOrDefault()?.NodeId == thai.Id && top[0].Hits == 2 && top[0].Title == thai.Title && top[0].LastAccessedAt.Kind == DateTimeKind.Utc,
              string.Join("; ", top.Select(t => $"{t.NodeId}×{t.Hits}")));
        return Task.CompletedTask;
    }

    private static Task MySqlLegacyUpgradeChecks()
    {
        if (SkipWithoutMySql()) return Task.CompletedTask;
        var cs = MySqlTestDb.NewDatabase();
        // The schema the node's MySQL store had before: space-split FULLTEXT, no event key.
        MySqlTestDb.Exec(cs, """
            CREATE TABLE nodes (id VARCHAR(64) PRIMARY KEY, title VARCHAR(512) NOT NULL, path TEXT NOT NULL,
                category VARCHAR(64), secondary_cats VARCHAR(512), tags VARCHAR(1024), word_count INT DEFAULT 0,
                importance DOUBLE DEFAULT 0, created_at DATETIME(6), modified_at DATETIME(6), preview TEXT,
                FULLTEXT KEY ft_title_preview_tags (title, preview, tags)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """);
        MySqlTestDb.Exec(cs, """
            CREATE TABLE access_log (id BIGINT AUTO_INCREMENT PRIMARY KEY, ts DATETIME(6) NOT NULL,
                node_id VARCHAR(64) NOT NULL, op VARCHAR(64), context TEXT, KEY ix_access_node_ts (node_id, ts))
                ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
            """);
        MySqlTestDb.Exec(cs, """
            INSERT INTO access_log (ts, node_id, op) VALUES
              ('2026-09-01 10:00:00.000001', 'a', 'read'), ('2026-09-01 10:00:00.000001', 'a', 'read'),
              ('2026-09-01 10:00:00.000001', 'a', 'read'), ('2026-09-01 10:00:01.000000', 'a', 'read')
            """);
        var s = new MySqlBrainStorage(cs);
        var threw = Record(s.Initialize);
        Check("Initialize upgrades an older schema without throwing", threw == null, Describe(threw));
        Check("the FULLTEXT index now uses ngram", MySqlTestDb.ShowCreate(cs, "nodes").Contains("ngram", StringComparison.OrdinalIgnoreCase));
        Check("the access log gained its event key", MySqlTestDb.ShowCreate(cs, "access_log").Contains("ux_access_event"));
        Check("duplicate events were dropped, distinct ones kept (4 rows → 2)",
              Convert.ToInt32(MySqlTestDb.Scalar(cs, "SELECT COUNT(*) FROM access_log")) == 2);
        Check("a third Initialize is a no-op", Record(s.Initialize) == null);
        return Task.CompletedTask;
    }

    private static async Task MySqlScopeChecks()
    {
        if (SkipWithoutMySql()) return;
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");   // the node's own locale
        try
        {
            var cs = MySqlTestDb.NewDatabase();
            var s = new MySqlBrainStorage(cs);
            s.Initialize();
            var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var expires = new DateTime(2027, 6, 7, 8, 9, 10, DateTimeKind.Utc);
            var scope = new ShareScope
            {
                OwnerAddress = "bx1Owner", PeerAddress = "bx1Peer", Level = ShareLevel.Preview,
                AllowCategories = [KnowledgeCategory.Programming, KnowledgeCategory.AI_MachineLearning],
                AllowTags = ["ดาว", "x"], AllowFolders = ["Notes/Public"], DenyTags = ["secret"], DenyFolders = ["Private"],
                NoteIdAllowlist = ["n1"], NoteIdBlocklist = ["n2"], ExpiresAt = expires,
                RequirePerNoteApproval = false, CreatedAt = created, OwnerSignature = [1, 2, 3, 250],
            };
            await s.UpsertScopeAsync(scope);
            var got = await s.GetScopeAsync("bx1Owner", "bx1Peer");
            Check("a stored scope reads back", got != null);
            if (got == null) return;
            Check("level, lists and the Thai tag survive",
                  got.Level == ShareLevel.Preview && got.AllowTags.SequenceEqual(["ดาว", "x"]) && got.AllowFolders.SequenceEqual(["Notes/Public"])
                  && got.DenyTags.SequenceEqual(["secret"]) && got.NoteIdAllowlist.SequenceEqual(["n1"]) && got.NoteIdBlocklist.SequenceEqual(["n2"]));
            Check("categories survive as names", got.AllowCategories.SequenceEqual([KnowledgeCategory.Programming, KnowledgeCategory.AI_MachineLearning]));
            Check("times come back as the same UTC instants (th-TH culture changes nothing)",
                  got.CreatedAt == created && got.ExpiresAt == expires && got.CreatedAt.Kind == DateTimeKind.Utc,
                  $"{got.CreatedAt:O} / {got.ExpiresAt:O}");
            Check("per-note approval flag and signature bytes survive",
                  !got.RequirePerNoteApproval && got.OwnerSignature.SequenceEqual(new byte[] { 1, 2, 3, 250 }));

            await Task.Delay(20);
            scope.Level = ShareLevel.MetadataOnly;
            scope.CreatedAt = DateTime.UtcNow;   // an update must not move the original grant time
            await s.UpsertScopeAsync(scope);
            var updated = await s.GetScopeAsync("bx1Owner", "bx1Peer");
            Check("an update changes the level and keeps the original created time",
                  updated?.Level == ShareLevel.MetadataOnly && updated.CreatedAt == created && updated.UpdatedAt > got.UpdatedAt,
                  $"{updated?.Level} {updated?.CreatedAt:O} {updated?.UpdatedAt:O}");

            await s.UpsertScopeAsync(new ShareScope { OwnerAddress = "bx1Owner", PeerAddress = "bx1Other", Level = ShareLevel.None });
            await s.UpsertScopeAsync(new ShareScope { OwnerAddress = "bx1Someone", PeerAddress = "bx1Peer", Level = ShareLevel.Preview });
            var list = await s.ListScopesAsync("bx1Owner");
            Check("ListScopesAsync returns this owner's scopes only", list.Count == 2 && list.All(x => x.OwnerAddress == "bx1Owner"), list.Count.ToString());
            Check("addresses compare exactly (a different case is a different address)",
                  await s.GetScopeAsync("BX1OWNER", "bx1Peer") == null);

            await s.DeleteScopeAsync("bx1Owner", "bx1Peer");
            Check("delete removes the grant (no row = deny)", await s.GetScopeAsync("bx1Owner", "bx1Peer") == null);
            Check("delete leaves the other grants", (await s.ListScopesAsync("bx1Owner")).Count == 1);
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    private static Task MySqlCloudStoreChecks()
    {
        if (SkipWithoutMySql()) return Task.CompletedTask;
        var cs = MySqlTestDb.NewDatabase();
        var store = CloudStore.ForMySql(cs);
        CloudStore.ForMySql(cs);   // a second node start on the same database
        Check("the MySQL store creates its tables and a second start is fine", store.ProviderName == "MySql");

        var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);
        var id = new string('a', 32);
        store.SaveAccount(new AccountRecord { Id = id, CreatedUtc = t0, KeyProtected = "blob", LicenseType = "monthly", LicenseStatus = "active", LicenseValid = true, ExpiresUtc = t0.AddDays(30), DaysRemaining = 30, CheckedUtc = t0, KeyHint = "ABCD" });
        store.SaveAccount(new AccountRecord { Id = id, CreatedUtc = t0.AddDays(5), KeyProtected = "blob2", LicenseType = "monthly", LicenseStatus = new string('s', 400), LicenseExpired = true, QuotaBytes = 5, Suspended = true });
        var a = store.GetAccount(id);
        Check("an update replaces the license fields and keeps created",
              a != null && a.CreatedUtc == t0 && a.KeyProtected == "blob2" && a.LicenseExpired && !a.LicenseValid && a.QuotaBytes == 5 && a.Suspended && a.ExpiresUtc == null,
              a?.ToString());
        Check("an over-long xman status is clipped, not a failed write", a?.LicenseStatus?.Length == 255);
        Check("ListAccounts sees it", store.ListAccounts().Count == 1);

        TokenRecord Tok(string tid, string kind, DateTimeOffset at) => new()
        {
            Id = tid, AccountId = id, Hash = CloudIds.HashToken("bxc_" + tid), Name = "device " + tid, Scope = CloudScopes.ReadWrite, Kind = kind, CreatedUtc = at,
        };
        for (var i = 0; i < CloudStore.MaxLiveTokensPerAccount; i++)
            store.TryInsertToken(Tok($"t{i:D11}", i == 5 ? CloudScopes.Device : CloudScopes.Api, t0.AddMinutes(i)), false, t0, out _);
        Check("20 live tokens", store.ListLiveTokens(id).Count == 20);
        Check("the 21st without eviction is refused", !store.TryInsertToken(Tok("t_extra00001", CloudScopes.Api, t0), false, t0, out _));
        var ok = store.TryInsertToken(Tok("t_extra00002", CloudScopes.Device, t0.AddHours(1)), true, t0.AddHours(1), out var evicted);
        Check("the 21st with eviction evicts the least recently used DEVICE token first",
              ok && evicted?.Id == "t00000000005" && store.ListLiveTokens(id).Count == 20, evicted?.Id);

        var hash = CloudIds.HashToken("bxc_t00000000007");
        Check("a live token is found by its exact hash", store.FindLiveTokenByHash(hash)?.Id == "t00000000007");
        Check("hashes compare byte for byte (upper-case → not found)", store.FindLiveTokenByHash(hash.ToUpperInvariant()) == null);

        var dup = Record(() => store.TryInsertToken(Tok("t00000000007", CloudScopes.Api, t0), true, t0, out _));
        Check("a duplicate token id is a unique violation IsUniqueViolation recognises",
              dup != null && CloudStore.IsUniqueViolation(dup), Describe(dup));

        Check("RevokeToken revokes once", store.RevokeToken(id, "t00000000007", t0) && !store.RevokeToken(id, "t00000000007", t0));
        store.TouchToken("t00000000008", t0.AddDays(2));
        var stats = store.TokenStatsByAccount();
        Check("stats: 19 live, last seen = the touch", stats.TryGetValue(id, out var st) && st.Live == 19 && st.LastSeenUtc == t0.AddDays(2),
              st?.ToString());
        Check("ListAllTokens includes the revoked ones (20 + 1, two revoked)",
              store.ListAllTokens(id) is { Count: 21 } all && all.Count(t => t.Revoked) == 2);
        var longName = store.TryInsertToken(Tok("t_longname01", CloudScopes.Api, t0) with { Name = new string('น', 400) }, true, t0, out _);
        Check("an over-long token name is clipped, not a failed login", longName && store.GetToken(id, "t_longname01")?.Name.Length == 255);
        Check("RevokeAllTokens revokes every live one", store.RevokeAllTokens(id, t0) == 20 && store.ListLiveTokens(id).Count == 0);
        store.DeleteAccount(id);
        Check("DeleteAccount removes the account and its tokens", store.GetAccount(id) == null && store.Count() == (0, 0));
        return Task.CompletedTask;
    }

    private static Task MySqlCloudMigrationChecks()
    {
        if (SkipWithoutMySql()) return Task.CompletedTask;
        var root = Path.Combine(Path.GetTempPath(), "brainx-cloud-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var t0 = DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000);
        var legacy = new CloudStore(Path.Combine(root, "cloud.db"));
        var acct = new string('b', 32);
        legacy.SaveAccount(new AccountRecord { Id = acct, CreatedUtc = t0, KeyProtected = "enc", LicenseType = "monthly", LicenseValid = true, KeyHint = "WXYZ", QuotaBytes = 7 });
        legacy.TryInsertToken(new TokenRecord { Id = "aaaaaaaaaaaa", AccountId = acct, Hash = CloudIds.HashToken("bxc_one"), Name = "pc", Scope = CloudScopes.ReadWrite, Kind = CloudScopes.Device, CreatedUtc = t0 }, false, t0, out _);
        legacy.TryInsertToken(new TokenRecord { Id = "bbbbbbbbbbbb", AccountId = acct, Hash = CloudIds.HashToken("bxc_two"), Name = "old", Scope = CloudScopes.Read, Kind = CloudScopes.Api, CreatedUtc = t0 }, false, t0, out _);
        legacy.RevokeToken(acct, "bbbbbbbbbbbb", t0.AddDays(1));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var cs = MySqlTestDb.NewDatabase();
        var clock = new ManualClock(t0);
        using (var svc = new CloudService(new CloudOptions { Root = root, MySqlConnString = cs }, new FakeXman(), clock))
        {
            Check("the service runs on MySQL", svc.Store.ProviderName == "MySql");
            var a = svc.Store.GetAccount(acct);
            Check("the account moved with its fields", a != null && a.KeyProtected == "enc" && a.KeyHint == "WXYZ" && a.QuotaBytes == 7 && a.CreatedUtc == t0);
            var all = svc.Store.ListAllTokens(acct);
            Check("both tokens moved, the revoked one still revoked",
                  all.Count == 2 && all.Single(t => t.Id == "bbbbbbbbbbbb").Revoked && !all.Single(t => t.Id == "aaaaaaaaaaaa").Revoked);
            Check("a moved token still authenticates", svc.Store.FindLiveTokenByHash(CloudIds.HashToken("bxc_one"))?.Id == "aaaaaaaaaaaa");
        }
        var leftovers = Directory.GetFiles(root, "cloud.db*").Select(Path.GetFileName).ToList();
        Check("cloud.db was renamed out of the way (nothing reads it again)",
              !File.Exists(Path.Combine(root, "cloud.db")) && leftovers.Any(f => f!.StartsWith("cloud.db.migrated-", StringComparison.Ordinal)),
              string.Join(", ", leftovers));

        using (var again = new CloudService(new CloudOptions { Root = root, MySqlConnString = cs }, new FakeXman(), clock))
            Check("a second start neither re-imports nor loses anything", again.Store.Count() == (1, 2));

        // A cloud.db appearing next to a MySQL store that already has accounts is never merged.
        var stray = new CloudStore(Path.Combine(root, "cloud.db"));
        stray.SaveAccount(new AccountRecord { Id = new string('c', 32), CreatedUtc = t0 });
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        using (var third = new CloudService(new CloudOptions { Root = root, MySqlConnString = cs }, new FakeXman(), clock))
        {
            Check("a full MySQL store takes nothing from a cloud.db", third.Store.Count() == (1, 2) && third.Store.GetAccount(new string('c', 32)) == null);
            Check("…and that cloud.db stays where it is", File.Exists(Path.Combine(root, "cloud.db")));
        }

        var bad = Record(() => new CloudService(new CloudOptions { Root = root + "-bad", MySqlConnString = "Server=127.0.0.1;Port=1;User ID=x;Password=y;Database=nope;Connection Timeout=2" }, new FakeXman(), clock));
        Check("an unreachable MySQL fails the start (the node runs without cloud, never on a second SQLite history)", bad != null, Describe(bad));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var dir in new[] { root, root + "-bad" })
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private static Exception? Record(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) { return ex; }
    }
}
