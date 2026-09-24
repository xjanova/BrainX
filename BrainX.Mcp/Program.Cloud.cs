using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BrainX.Core.Services;
using BrainX.Core.Services.Cloud;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

/// <summary>
/// BrainX Cloud from the command line and in the MCP server.
///
/// CLI — `brainx-mcp cloud …`:
///   login [key|-] [--device NAME]   sign this machine in (the key is never stored; only its last 4)
///   status                          account, license, usage, cache
///   logout                          revoke this machine's token and forget it
///   pull [--allow-mass-delete]      mirror the account into the local cache now
///   push --vault DIR [--folders a,b | --all] [--remove-deselected] [--allow-mass-delete]
///                                   upload a vault's chosen folders (servers without the desktop app)
///   token create NAME [--read]      an access token for another machine (shown once)
///   token list | token revoke ID
///
/// SERVE — `brainx-mcp --cloud` (or BRAINX_CLOUD=1): the vault is the account's
/// cache (%LOCALAPPDATA%\BrainX\cloud-cache\&lt;accountId&gt;). Startup pulls what
/// changed (bounded — offline means "serve the cache and say so"), re-indexes
/// in-process when anything did, and every write tool's result is pushed back
/// in the background: queued, retried, never holding up a reply.
/// </summary>
internal static partial class Program
{
    // ── serve-mode state ─────────────────────────────────────────────
    private static bool _cloudMode;
    private static CloudApiClient? _cloudApi;
    private static CloudSyncEngine? _cloudEngine;
    private static string? _cloudAccountId;
    private static volatile string _cloudStatus = "";
    private static DateTime? _cloudLastPullUtc;
    private static DateTime? _cloudLastPushUtc;
    private static volatile bool _cloudOnline;
    private static int _cloudPendingPushes;
    private static readonly SemaphoreSlim _cloudSignal = new(0);
    private static readonly CancellationTokenSource _cloudCts = new();
    private static readonly object _cloudExportGate = new();
    private static Task? _cloudWorker;

    private static readonly TimeSpan CloudPullInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CloudSafetyPushInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tools that write notes. Pushing after anything else is harmless (the
    /// push is a no-op when the cache is clean) but pointless; a writer missing
    /// from this list is still caught by the safety pass every few minutes.
    /// </summary>
    private static readonly HashSet<string> CloudWriteTools = new(StringComparer.Ordinal)
    {
        "brain_create_note", "brain_append_note", "brain_remember", "brain_import_path",
        "brain_mark_verified", "brain_apply_audit_fix", "brain_dream", "brain_synthesize",
        "task_handoff", "task_update", "submit_for_review", "post_review_verdict",
    };

    internal static bool IsCloudServeRequested(string[] args) =>
        args.Contains("--cloud", StringComparer.OrdinalIgnoreCase)
        || Environment.GetEnvironmentVariable("BRAINX_CLOUD") == "1";

    // ═════════════════════════════════════════════════════════════════
    // CLI
    // ═════════════════════════════════════════════════════════════════

    internal static async Task<int> CloudCliAsync(string[] args)
    {
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var rest = args.Skip(1).ToArray();
        try
        {
            return verb switch
            {
                "login" => await CloudLoginCliAsync(rest).ConfigureAwait(false),
                "status" => await CloudStatusCliAsync().ConfigureAwait(false),
                "logout" => await CloudLogoutCliAsync().ConfigureAwait(false),
                "pull" => await CloudPullCliAsync(rest).ConfigureAwait(false),
                "push" => await CloudPushCliAsync(rest).ConfigureAwait(false),
                "token" or "tokens" => await CloudTokenCliAsync(rest).ConfigureAwait(false),
                "selftest" => await CloudSelfTest.RunAsync(rest).ConfigureAwait(false),
                _ => CloudHelp(),
            };
        }
        catch (CloudApiException ex)
        {
            Console.Error.WriteLine("✗ " + CloudFriendly(ex.Code));
            return 2;
        }
    }

    private static int CloudHelp()
    {
        Console.WriteLine("BrainX Cloud — your chosen notes, reachable from any Claude");
        Console.WriteLine();
        Console.WriteLine("  brainx-mcp cloud login [KEY|-] [--device NAME]  sign this machine in ('-' reads the key from stdin;");
        Console.WriteLine("                                                  no KEY prompts for it). The key is never stored.");
        Console.WriteLine("  brainx-mcp cloud status                         account, license, storage, local cache");
        Console.WriteLine("  brainx-mcp cloud logout                         revoke this machine's access and forget it");
        Console.WriteLine("  brainx-mcp cloud pull [--allow-mass-delete]     refresh the local cache from the cloud now");
        Console.WriteLine("  brainx-mcp cloud push --vault DIR [--folders A,B | --all] [--remove-deselected] [--allow-mass-delete]");
        Console.WriteLine("                                                  upload a vault's chosen top-level folders ('/' = root notes)");
        Console.WriteLine("  brainx-mcp cloud token create NAME [--read]     access token for another machine (shown once)");
        Console.WriteLine("  brainx-mcp cloud token list | revoke ID");
        Console.WriteLine();
        Console.WriteLine("Serve the cloud brain to Claude from this machine (no local vault needed):");
        Console.WriteLine("  brainx-mcp register-claude --cloud              registers \"brainx-cloud\" in Claude Code + Desktop");
        Console.WriteLine("  brainx-mcp --cloud                              (what that registration runs; or set BRAINX_CLOUD=1)");
        Console.WriteLine();
        Console.WriteLine("Or with nothing installed at all:");
        Console.WriteLine($"  claude mcp add --transport http brainx {CloudEndpoints.RemoteMcpUrl(CloudEndpoints.DefaultBaseUrl)} --header \"Authorization: Bearer <token>\"");
        Console.WriteLine();
        Console.WriteLine($"Buy or renew ({CloudEndpoints.PriceLabel}): {CloudEndpoints.BuyUrl}");
        return 0;
    }

    private static async Task<int> CloudLoginCliAsync(string[] a)
    {
        string? key = null;
        var device = Environment.MachineName;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] == "--device" && i + 1 < a.Length) device = a[++i];
            else if (key == null) key = a[i];
        }
        if (key == "-") key = Console.In.ReadLine();
        if (string.IsNullOrWhiteSpace(key))
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("✗ No license key given. Usage: brainx-mcp cloud login <KEY>  (or '-' to read it from stdin)");
                return 1;
            }
            key = ReadSecret("License key: ");
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            Console.Error.WriteLine("✗ " + CloudFriendly(CloudErrorCodes.InvalidLicense));
            return 1;
        }
        device = string.IsNullOrWhiteSpace(device) ? "brainx-mcp" : device.Trim();
        if (device.Length > 64) device = device[..64];

        var store = new CloudCredentialStore();
        var previous = store.Load();
        using var api = new CloudApiClient();
        var login = await api.LoginAsync(key, device).ConfigureAwait(false);
        var rec = store.SaveLogin(login, device, key);
        key = null;   // not kept past this point

        // Signing in again on the same machine: the old device token would sit
        // on the account's 20-token budget forever. Retire it, best effort.
        if (previous?.Token is { Length: > 0 } oldToken && previous.TokenId != rec.TokenId)
        {
            try
            {
                using var old = new CloudApiClient(oldToken, api.BaseUrl);
                await old.LogoutAsync().ConfigureAwait(false);
            }
            catch { /* already revoked, or offline — it expires with nothing using it */ }
        }

        Console.WriteLine($"✓ Signed in to BrainX Cloud as \"{device}\"");
        PrintAccount(login.Account, rec.LicenseKeyHint, cached: false);
        Console.WriteLine();
        Console.WriteLine("Next: brainx-mcp register-claude --cloud   (serve these notes to Claude from this machine)");
        return 0;
    }

    private static async Task<int> CloudStatusCliAsync()
    {
        var store = new CloudCredentialStore();
        var rec = store.Load();
        var envToken = Environment.GetEnvironmentVariable("BRAINX_CLOUD_TOKEN");
        if (rec == null || !rec.IsSignedIn)
        {
            if (!string.IsNullOrWhiteSpace(envToken))
                Console.WriteLine("BrainX Cloud · using BRAINX_CLOUD_TOKEN from the environment (no stored sign-in)");
            else
            {
                Console.WriteLine("BrainX Cloud · not signed in on this machine");
                Console.WriteLine("  brainx-mcp cloud login <license key>");
                Console.WriteLine($"  Buy or renew ({CloudEndpoints.PriceLabel}): {CloudEndpoints.BuyUrl}");
                return 1;
            }
        }
        else
        {
            Console.WriteLine($"BrainX Cloud · signed in as \"{rec.DeviceName}\"");
        }

        var token = !string.IsNullOrWhiteSpace(envToken) ? envToken.Trim() : rec!.Token;
        using var api = new CloudApiClient(token);
        if (api.IsDevOverride) Console.WriteLine($"  server:  {api.BaseUrl}  (BRAINX_CLOUD_URL override)");
        CloudAccount? account = null;
        try
        {
            account = await api.GetAccountAsync().ConfigureAwait(false);
            if (rec != null && string.IsNullOrWhiteSpace(envToken)) store.UpdateAccount(account);
            PrintAccount(account, rec?.LicenseKeyHint, cached: false);
        }
        catch (CloudApiException ex)
        {
            Console.WriteLine("  ⚠ " + CloudFriendly(ex.Code));
            if (rec?.LastAccount != null)
            {
                Console.WriteLine($"  last known ({FormatUtc(rec.LastAccountUtc)}):");
                PrintAccount(rec.LastAccount, rec.LicenseKeyHint, cached: true);
            }
        }

        var accountId = account?.Id ?? rec?.AccountId;
        if (!string.IsNullOrEmpty(accountId))
        {
            var cache = CloudCredentialStore.CacheDirFor(accountId);
            var st = Directory.Exists(cache) ? CloudSyncState.Load(cache) : null;
            Console.WriteLine($"  cache:   {cache}");
            if (st?.LastSync != null)
                Console.WriteLine($"           {st.Uploaded.Count:N0} note(s) · last sync {FormatUtc(st.LastSync.Utc)}"
                                + (st.LastSync.Ok ? "" : $" ({st.LastSync.ErrorCode})"));
            else
                Console.WriteLine("           not pulled yet — `brainx-mcp cloud pull`, or start `brainx-mcp --cloud`");
        }
        return 0;
    }

    private static async Task<int> CloudLogoutCliAsync()
    {
        var store = new CloudCredentialStore();
        var rec = store.Load();
        if (rec?.Token == null)
        {
            store.Clear();
            Console.WriteLine("Not signed in — nothing to do.");
            return 0;
        }
        var revoked = false;
        try
        {
            using var api = new CloudApiClient(rec.Token);
            await api.LogoutAsync().ConfigureAwait(false);
            revoked = true;
        }
        catch (CloudApiException ex) when (ex.IsAuthFailure) { revoked = true; }   // already revoked
        catch (CloudApiException ex)
        {
            Console.WriteLine("  ⚠ " + CloudFriendly(ex.Code));
        }
        store.Clear();
        Console.WriteLine(revoked
            ? "✓ Signed out. This machine's access token is revoked."
            : "✓ Signed out on this machine. The cloud could not be reached, so its token is still live —\n" +
              "  revoke it from BrainX ▸ Settings ▸ BrainX Cloud on another machine (token list).");
        return 0;
    }

    private static async Task<int> CloudPullCliAsync(string[] a)
    {
        var (api, accountId) = RequireSignIn();
        using (api)
        {
            var cache = CloudCredentialStore.CacheDirFor(accountId);
            var engine = new CloudSyncEngine(api);
            Console.WriteLine($"Pulling into {cache} …");
            var r = await engine.PullAsync(cache, new CloudPullOptions
            {
                AccountId = accountId,
                AllowMassDelete = a.Contains("--allow-mass-delete"),
            }, ConsoleProgress()).ConfigureAwait(false);
            PrintPull(r);
            if (!r.Ok) return 3;
            if (r.Changed || !File.Exists(Path.Combine(cache, ".obsidianx", "brain-export.json")))
            {
                Console.WriteLine("Re-indexing the cache …");
                var n = ExportVault(cache);
                Console.WriteLine($"✓ indexed {n:N0} note(s)");
            }
            return 0;
        }
    }

    private static async Task<int> CloudPushCliAsync(string[] a)
    {
        string? vault = null, foldersArg = null;
        bool all = false, removeDeselected = false, allowMass = false;
        for (var i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--vault" when i + 1 < a.Length: vault = a[++i]; break;
                case "--folders" when i + 1 < a.Length: foldersArg = a[++i]; break;
                case "--all": all = true; break;
                case "--remove-deselected": removeDeselected = true; break;
                case "--allow-mass-delete": allowMass = true; break;
            }
        }
        if (string.IsNullOrWhiteSpace(vault) || !Directory.Exists(vault))
        {
            Console.Error.WriteLine("✗ --vault DIR is required and must exist.");
            return 1;
        }
        vault = Path.GetFullPath(vault);
        var (api, accountId) = RequireSignIn();
        using (api)
        {
            var state = CloudSyncState.Load(vault);
            List<string> folders;
            if (all)
            {
                folders = CloudSyncEngine.TopLevelFolders(vault);
                folders.Add(CloudSyncState.RootToken);
            }
            else if (foldersArg != null)
            {
                folders = foldersArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(f => f is "/" or "." or "(root)" ? CloudSyncState.RootToken : f.Trim('/', '\\'))
                    .Distinct(StringComparer.Ordinal).ToList();
                var bad = folders.Where(f => f != CloudSyncState.RootToken && !CloudPathRules.IsSelectableFolderName(f)).ToList();
                if (bad.Count > 0)
                {
                    Console.Error.WriteLine($"✗ Not a usable top-level folder name: {string.Join(", ", bad)}");
                    return 1;
                }
            }
            else folders = state.Folders.ToList();

            if (folders.Count == 0)
            {
                Console.Error.WriteLine("✗ No folders chosen. Use --folders A,B (\"/\" = notes in the vault root) or --all.");
                return 1;
            }

            // Folders dropped from the selection: their notes stay in the cloud
            // unless the owner says otherwise — a CLI cannot ask, so it needs a flag.
            if (string.Equals(state.AccountId, accountId, StringComparison.Ordinal))
            {
                foreach (var dropped in state.Folders.Except(folders, StringComparer.Ordinal).ToList())
                {
                    var owned = state.UploadedUnder(dropped);
                    if (owned.Count == 0) continue;
                    if (removeDeselected)
                        Console.WriteLine($"  {LabelFolder(dropped)}: {owned.Count} note(s) will be removed from the cloud");
                    else
                    {
                        foreach (var p in owned) state.Uploaded.Remove(p);
                        Console.WriteLine($"  {LabelFolder(dropped)}: {owned.Count} note(s) stay in the cloud but no longer sync " +
                                          "(--remove-deselected deletes them)");
                    }
                }
            }
            state.Folders = folders;
            state.Save(vault);

            var engine = new CloudSyncEngine(api);
            Console.WriteLine($"Pushing {string.Join(", ", folders.Select(LabelFolder))} from {vault} …");
            var r = await engine.PushAsync(vault, folders, state, new CloudPushOptions
            {
                AccountId = accountId,
                AllowMassDelete = allowMass,
            }, ConsoleProgress()).ConfigureAwait(false);
            PrintPush(r);
            return r.Ok ? (r.HeldBackDeletes > 0 ? 4 : 0) : 3;
        }
    }

    private static async Task<int> CloudTokenCliAsync(string[] a)
    {
        var sub = a.Length > 0 ? a[0].ToLowerInvariant() : "list";
        var (api, _) = RequireSignIn();
        using (api)
        {
            switch (sub)
            {
                case "create":
                {
                    var name = a.Skip(1).FirstOrDefault(x => !x.StartsWith("--"));
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Console.Error.WriteLine("✗ Usage: brainx-mcp cloud token create NAME [--read]");
                        return 1;
                    }
                    var scope = a.Contains("--read") ? CloudScopes.Read : CloudScopes.ReadWrite;
                    var t = await api.CreateTokenAsync(name.Trim(), scope).ConfigureAwait(false);
                    Console.WriteLine($"✓ Token \"{name}\" ({scope}) — shown ONCE, store it safely:");
                    Console.WriteLine();
                    Console.WriteLine("  " + t.Token);
                    Console.WriteLine();
                    Console.WriteLine("Connect Claude Code on the other machine (nothing to install):");
                    Console.WriteLine("  " + CloudEndpoints.ClaudeMcpAddCommand(api.BaseUrl, t.Token));
                    return 0;
                }
                case "revoke":
                {
                    var id = a.Skip(1).FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        Console.Error.WriteLine("✗ Usage: brainx-mcp cloud token revoke ID   (IDs: brainx-mcp cloud token list)");
                        return 1;
                    }
                    await api.RevokeTokenAsync(id.Trim()).ConfigureAwait(false);
                    Console.WriteLine($"✓ Token {id} revoked.");
                    return 0;
                }
                default:
                {
                    var tokens = await api.ListTokensAsync().ConfigureAwait(false);
                    var mine = new CloudCredentialStore().Load()?.TokenId;
                    Console.WriteLine($"{tokens.Count} live token(s):");
                    foreach (var t in tokens)
                        Console.WriteLine($"  {t.Id,-12} {t.Name,-24} {t.Scope,-9} {t.Kind,-6} " +
                                          $"created {FormatUtc(t.CreatedUtc)} · last used {FormatUtc(t.LastUsedUtc)}" +
                                          (t.Id == mine ? "  ← this machine" : ""));
                    return 0;
                }
            }
        }
    }

    private static (CloudApiClient Api, string AccountId) RequireSignIn()
    {
        var rec = new CloudCredentialStore().Load();
        if (rec == null || !rec.IsSignedIn)
            throw new CloudApiException(CloudErrorCodes.NotSignedIn, "not signed in");
        return (new CloudApiClient(rec.Token), rec.AccountId!);
    }

    private static IProgress<CloudSyncProgress> ConsoleProgress()
    {
        string? lastPhase = null;
        var lastDone = -1;
        // Synchronous IProgress: Progress<T> would post to a thread pool with no
        // ordering, and a console reader wants the lines in order.
        return new SyncProgress(p =>
        {
            if (p.Phase == lastPhase && p.Done - lastDone < 200 && p.Done != p.Total) return;
            lastPhase = p.Phase;
            lastDone = p.Done;
            if (p.Total > 0) Console.WriteLine($"  {p.Phase,-8} {p.Done:N0}/{p.Total:N0}");
            else Console.WriteLine($"  {p.Phase}…");
        });
    }

    private sealed class SyncProgress(Action<CloudSyncProgress> report) : IProgress<CloudSyncProgress>
    {
        public void Report(CloudSyncProgress value) => report(value);
    }

    private static void PrintAccount(CloudAccount acct, string? keyHint, bool cached)
    {
        var inv = CultureInfo.InvariantCulture;
        var pad = cached ? "    " : "  ";
        Console.WriteLine($"{pad}license: {acct.LicenseType ?? "?"} · {(acct.IsValid ? "valid" : "EXPIRED")}"
                          + (acct.ExpiresUtc is { } e ? $" · until {e.ToString("yyyy-MM-dd", inv)}" : "")
                          + (acct.DaysRemaining is { } d ? $" ({d} day(s) left)" : "")
                          + (string.IsNullOrEmpty(keyHint) ? "" : $" · key …{keyHint}"));
        Console.WriteLine($"{pad}storage: {FormatBytesCloud(acct.UsedBytes)} of {FormatBytesCloud(acct.QuotaBytes)} · {acct.NoteCount:N0} note(s)");
        if (!acct.IsValid)
            Console.WriteLine($"{pad}renew:   {CloudEndpoints.BuyUrl}");
    }

    private static void PrintPush(CloudPushResult r)
    {
        if (r.Cancelled) { Console.WriteLine("⚠ cancelled — the next push continues from here"); return; }
        if (!r.Ok) Console.WriteLine("✗ " + CloudFriendly(r.ErrorCode));
        Console.WriteLine($"  uploaded {r.Uploaded:N0} · deleted {r.Deleted:N0} · unchanged {r.Unchanged:N0} of {r.LocalNotes:N0} local note(s)");
        if (r.KeptCloudVersion > 0)
            Console.WriteLine($"  {r.KeptCloudVersion:N0} note(s) were edited in the cloud since this vault sent them — the cloud's copy was kept");
        if (r.HeldBackDeletes > 0)
            Console.WriteLine($"  ⚠ {r.HeldBackDeletes:N0} note(s) vanished from the selected folders — NOT deleted from the cloud. " +
                              "If that is intended, run again with --allow-mass-delete");
        foreach (var f in r.MissingFolders)
            Console.WriteLine($"  ⚠ selected folder \"{f}\" does not exist here — its cloud notes were left alone");
        if (r.Ignored > 0) Console.WriteLine($"  {r.Ignored:N0} note(s) left out by .brainxignore");
        foreach (var s in r.Skipped.Take(15)) Console.WriteLine($"  skipped {s.Path}: {s.Reason}");
        if (r.Skipped.Count > 15) Console.WriteLine($"  … and {r.Skipped.Count - 15} more skipped");
        if (r.UsedBytes is { } u) Console.WriteLine($"  cloud storage: {FormatBytesCloud(u)}" + (r.QuotaBytes is { } q && q > 0 ? $" of {FormatBytesCloud(q)}" : ""));
    }

    private static void PrintPull(CloudPullResult r)
    {
        if (r.Cancelled) { Console.WriteLine("⚠ cancelled"); return; }
        if (!r.Ok) Console.WriteLine("✗ " + CloudFriendly(r.ErrorCode));
        Console.WriteLine($"  {r.ServerNotes:N0} note(s) in the cloud · fetched {r.Fetched:N0} · removed {r.DeletedLocal:N0} · unchanged {r.Unchanged:N0}");
        if (r.KeptLocal > 0) Console.WriteLine($"  {r.KeptLocal:N0} local change(s) not pushed yet were kept");
        if (r.HeldBackDeletes > 0)
            Console.WriteLine($"  ⚠ the cloud no longer lists {r.HeldBackDeletes:N0} cached note(s) — kept (use --allow-mass-delete to remove)");
        foreach (var s in r.Skipped.Take(10)) Console.WriteLine($"  skipped {s.Path}: {s.Reason}");
    }

    private static string LabelFolder(string f) => f == CloudSyncState.RootToken ? "(vault root notes)" : f;

    /// <summary>What a person should read for an error code. English — the CLI is English throughout.</summary>
    internal static string CloudFriendly(string? code) => code switch
    {
        CloudErrorCodes.InvalidLicense => "That license key is not valid. Check it, or buy one at " + CloudEndpoints.BuyUrl,
        CloudErrorCodes.LicenseExpired => "The BrainX Cloud license has expired. Renew at " + CloudEndpoints.BuyUrl +
                                          " — your notes stay in the cloud and can still be downloaded.",
        CloudErrorCodes.LicenseServerUnreachable => "The license could not be verified right now (license server unreachable). Try again in a few minutes.",
        CloudErrorCodes.RateLimited => "Too many attempts — wait a minute and try again.",
        CloudErrorCodes.QuotaExceeded => "The cloud space is full. Deselect folders or remove notes, then sync again.",
        CloudErrorCodes.Network or CloudErrorCodes.Timeout => "BrainX Cloud cannot be reached. Check the internet connection; nothing was lost.",
        CloudErrorCodes.Unauthorized => "This machine's sign-in is no longer valid (it may have been revoked). Sign in again: brainx-mcp cloud login",
        CloudErrorCodes.Forbidden => "This access token is not allowed to do that (read-only, or not a device sign-in).",
        CloudErrorCodes.NotSignedIn => "Not signed in. Run: brainx-mcp cloud login <license key>",
        CloudSyncResult.Busy => "Another sync of the same folder is running — try again in a moment.",
        CloudSyncResult.VaultMissing => "The vault folder was not found.",
        CloudSyncResult.LocalIo => "A local file could not be read or written.",
        null => "",
        _ => $"BrainX Cloud returned an unexpected error ({code}). Try again later.",
    };

    private static string FormatBytesCloud(long b) =>
        b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
      : b >= 1L << 20 ? (b / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB"
      : b >= 1L << 10 ? (b / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB"
      : b + " B";

    private static string FormatUtc(DateTime? utc) =>
        utc is { } u ? u.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never";

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    /// <summary>Re-index <paramref name="vault"/> and rewrite its brain-export.json (the `export` command's core).</summary>
    private static int ExportVault(string vault)
    {
        var graph = new KnowledgeIndexer().IndexVault(vault);
        var result = new BrainExporter().Export(vault, LoadOrAnonymousIdentity(vault), graph);
        return result.NodeCount;
    }

    // ═════════════════════════════════════════════════════════════════
    // SERVE MODE
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// Point this server at the account's cache and bring the cache up to date,
    /// waiting at most BRAINX_CLOUD_STARTUP_WAIT_SEC (default 20 s) — a client
    /// that times the server out would be worse than one served a stale cache.
    /// </summary>
    internal static async Task StartCloudServeAsync()
    {
        _cloudMode = true;
        var store = new CloudCredentialStore();
        var rec = store.Load();
        var envToken = Environment.GetEnvironmentVariable("BRAINX_CLOUD_TOKEN");
        var fromEnv = !string.IsNullOrWhiteSpace(envToken);
        var token = fromEnv ? envToken!.Trim() : rec?.Token;
        var accountId = fromEnv ? EnvTokenAccount(token!) : rec?.AccountId;

        _cloudApi = new CloudApiClient(token);
        _cloudEngine = new CloudSyncEngine(_cloudApi);
        if (_cloudApi.IsDevOverride) Log($"cloud: server override active (BRAINX_CLOUD_URL) → {_cloudApi.BaseUrl}");

        if (string.IsNullOrEmpty(token))
        {
            UseCloudPlaceholderVault("_signed-out");
            _cloudStatus = "not signed in — run `brainx-mcp cloud login <license key>` on this machine";
            Log("cloud: " + _cloudStatus);
            return;
        }

        if (string.IsNullOrEmpty(accountId))
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var acct = await _cloudApi.GetAccountAsync(cts.Token).ConfigureAwait(false);
                accountId = acct.Id;
                _cloudOnline = true;
                if (fromEnv) RememberEnvTokenAccount(token!, acct.Id);
            }
            catch (CloudApiException ex)
            {
                Log($"cloud: could not identify the account ({ex.Code})");
                _cloudStatus = CloudFriendly(ex.Code);
            }
        }
        if (string.IsNullOrEmpty(accountId))
        {
            UseCloudPlaceholderVault("_unknown");
            if (string.IsNullOrEmpty(_cloudStatus))
                _cloudStatus = "BrainX Cloud unreachable and this token was never used here — serving an empty brain until it is reachable";
            Log("cloud: " + _cloudStatus);
            return;
        }

        _cloudAccountId = accountId;
        _vaultPath = CloudCredentialStore.CacheDirFor(accountId);
        Directory.CreateDirectory(_vaultPath);
        _cloudStatus = "starting";
        Log($"cloud mode · account {ShortId(accountId)} · cache {_vaultPath}");

        var wait = TimeSpan.FromSeconds(
            int.TryParse(Environment.GetEnvironmentVariable("BRAINX_CLOUD_STARTUP_WAIT_SEC"), out var s) && s is >= 0 and <= 300 ? s : 20);
        var startup = Task.Run(() => CloudPullAndIndexAsync("startup", _cloudCts.Token));
        if (await Task.WhenAny(startup, Task.Delay(wait)).ConfigureAwait(false) != startup)
        {
            Log($"cloud: first sync still running after {wait.TotalSeconds:0}s — serving the cache as it is; it finishes in the background");
            // Tools need an index to answer at all; give them the cache as it stands.
            if (!File.Exists(Path.Combine(_vaultPath, ".obsidianx", "brain-export.json"))) RunCloudExport("partial cache");
        }

        _cloudWorker = Task.Run(() => CloudWorkerLoopAsync(startup, _cloudCts.Token));
    }

    private static void UseCloudPlaceholderVault(string name)
    {
        _vaultPath = Path.Combine(CloudCredentialStore.CacheRoot, name);
        try
        {
            Directory.CreateDirectory(_vaultPath);
            if (!File.Exists(Path.Combine(_vaultPath, ".obsidianx", "brain-export.json"))) RunCloudExport("empty");
        }
        catch (Exception ex) { Log($"cloud: placeholder vault: {ex.Message}"); }
    }

    private static async Task CloudPullAndIndexAsync(string reason, CancellationToken ct)
    {
        if (_cloudEngine == null || _cloudAccountId == null) return;
        CloudPullResult r;
        try
        {
            r = await _cloudEngine.PullAsync(_vaultPath, new CloudPullOptions
            {
                AccountId = _cloudAccountId,
                FileLock = _requestGate,
                LockTimeout = TimeSpan.FromSeconds(15),
            }, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"cloud pull ({reason}) failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (r.Ok)
        {
            _cloudOnline = true;
            _cloudLastPullUtc = DateTime.UtcNow;
            _cloudStatus = $"in sync · {r.ServerNotes:N0} note(s) in the cloud";
            Log($"cloud pull ({reason}): {r.ServerNotes} in cloud · fetched {r.Fetched} · removed {r.DeletedLocal} · " +
                $"kept {r.KeptLocal} local change(s) · {r.Duration.TotalSeconds:0.0}s");
            if (r.HeldBackDeletes > 0)
                Log($"cloud pull: the cloud no longer lists {r.HeldBackDeletes} cached note(s) — kept; `brainx-mcp cloud pull --allow-mass-delete` removes them");
        }
        else if (r.Cancelled) return;
        else
        {
            if (r.ErrorCode is CloudErrorCodes.Network or CloudErrorCodes.Timeout) _cloudOnline = false;
            _cloudStatus = r.ErrorCode switch
            {
                CloudErrorCodes.Network or CloudErrorCodes.Timeout =>
                    "offline — serving the cached notes" + (_cloudLastPullUtc is { } t ? $" (last sync {FormatUtc(t)})" : ""),
                CloudErrorCodes.LicenseExpired => "license expired — serving the cached notes; renew at " + CloudEndpoints.BuyUrl,
                _ => CloudFriendly(r.ErrorCode),
            };
            Log($"cloud pull ({reason}): {r.ErrorCode} — {_cloudStatus}. {r.ErrorMessage}");
        }

        if (r.Changed || !File.Exists(Path.Combine(_vaultPath, ".obsidianx", "brain-export.json")))
            RunCloudExport(reason);
        // Local writes that never made it up (offline last session, killed mid-push).
        if (r.KeptLocal > 0) KickCloudPush();
    }

    private static void RunCloudExport(string reason)
    {
        lock (_cloudExportGate)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var n = ExportVault(_vaultPath);
                Log($"cloud: re-indexed cache ({reason}) — {n:N0} note(s) in {sw.Elapsed.TotalSeconds:0.0}s");
            }
            catch (Exception ex) { Log($"cloud: re-index failed ({reason}): {ex.Message}"); }
        }
    }

    /// <summary>Called after every successful tool call in cloud mode.</summary>
    private static void CloudNoteToolCall(string? tool)
    {
        if (!_cloudMode || tool == null || !CloudWriteTools.Contains(tool)) return;
        KickCloudPush();
    }

    private static void KickCloudPush()
    {
        if (!_cloudMode || _cloudEngine == null || _cloudAccountId == null) return;
        Interlocked.Exchange(ref _cloudPendingPushes, 1);
        try { _cloudSignal.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>
    /// Background: pushes after writes (1.5 s debounce, retried with backoff),
    /// a safety push check every 5 min, a pull every 10 min.
    /// </summary>
    private static async Task CloudWorkerLoopAsync(Task startup, CancellationToken ct)
    {
        try { await startup.ConfigureAwait(false); } catch { }
        var nextPull = DateTime.UtcNow + CloudPullInterval;
        var nextSafety = DateTime.UtcNow + CloudSafetyPushInterval;
        DateTime? retryAt = null;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var due = new[] { nextPull, nextSafety, retryAt ?? DateTime.MaxValue }.Min();
            var delay = due > now ? due - now : TimeSpan.Zero;
            if (delay > TimeSpan.FromMinutes(10)) delay = TimeSpan.FromMinutes(10);
            bool signalled;
            try { signalled = await _cloudSignal.WaitAsync(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            if (signalled)
            {
                // Debounce a burst of writes into one push.
                try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                while (_cloudSignal.CurrentCount > 0) await _cloudSignal.WaitAsync(0, ct).ConfigureAwait(false);
            }

            now = DateTime.UtcNow;
            var wantPush = Volatile.Read(ref _cloudPendingPushes) == 1 && (retryAt == null || now >= retryAt || signalled);
            if (now >= nextSafety) { wantPush = true; nextSafety = now + CloudSafetyPushInterval; }
            if (wantPush)
            {
                var outcome = await CloudPushOnceAsync(ct).ConfigureAwait(false);
                if (outcome == PushOutcome.Retry)
                {
                    failures++;
                    var backoff = TimeSpan.FromSeconds(Math.Min(900, 10 * Math.Pow(3, Math.Min(failures - 1, 5))));
                    retryAt = DateTime.UtcNow + backoff;
                }
                else { failures = 0; retryAt = null; }
            }
            if (DateTime.UtcNow >= nextPull)
            {
                await CloudPullAndIndexAsync("periodic", ct).ConfigureAwait(false);
                nextPull = DateTime.UtcNow + CloudPullInterval;
            }
        }
    }

    private enum PushOutcome { Done, Retry, GiveUp }

    private static async Task<PushOutcome> CloudPushOnceAsync(CancellationToken ct)
    {
        if (_cloudEngine == null || _cloudAccountId == null) return PushOutcome.GiveUp;
        Interlocked.Exchange(ref _cloudPendingPushes, 0);
        CloudPushResult r;
        try
        {
            // Fresh from disk: another brainx-mcp on the same cache may have
            // pushed or pulled since; the engine reloads it again under the lock.
            var state = CloudSyncState.Load(_vaultPath);
            r = await _cloudEngine.PushAsync(_vaultPath, null, state, new CloudPushOptions
            {
                AccountId = _cloudAccountId,
                // The cache mirrors the cloud; a note missing here is not a
                // decision to delete it there (no tool deletes notes).
                AllowDeletes = false,
                HonourIgnoreFile = false,
                SkipIfClean = true,
                FileLock = _requestGate,
                LockTimeout = TimeSpan.FromSeconds(10),
            }, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _cloudPendingPushes, 1);
            return PushOutcome.GiveUp;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _cloudPendingPushes, 1);
            Log($"cloud push failed: {ex.GetType().Name}: {ex.Message}");
            return PushOutcome.Retry;
        }

        if (r.NothingToDo) return PushOutcome.Done;
        if (r.Ok)
        {
            _cloudOnline = true;
            _cloudLastPushUtc = DateTime.UtcNow;
            if (r.Uploaded > 0) Log($"cloud push: sent {r.Uploaded} note(s)");
            foreach (var s in r.Skipped.Take(5)) Log($"cloud push: skipped {s.Path}: {s.Reason}");
            return PushOutcome.Done;
        }
        if (r.Cancelled) return PushOutcome.GiveUp;

        if (r.ErrorCode is CloudSyncResult.Busy)
        {
            Interlocked.Exchange(ref _cloudPendingPushes, 1);
            return PushOutcome.Retry;
        }
        if (r.ErrorCode is CloudErrorCodes.Network or CloudErrorCodes.Timeout) _cloudOnline = false;
        Log($"cloud push: {r.ErrorCode} — {CloudFriendly(r.ErrorCode)} (the notes stay in the cache and are retried)");
        // A license, permission or quota problem will not fix itself in
        // seconds, and retrying it in a loop would only hammer the server. The
        // notes stay in the cache; the next write or the 5-minute safety pass
        // tries again (the push is dirty-checked, so they are found then).
        if (r.ErrorCode is CloudErrorCodes.LicenseExpired or CloudErrorCodes.Forbidden
                        or CloudErrorCodes.Unauthorized or CloudErrorCodes.QuotaExceeded)
            return PushOutcome.GiveUp;
        Interlocked.Exchange(ref _cloudPendingPushes, 1);
        return PushOutcome.Retry;
    }

    /// <summary>The client hung up: one last push attempt, bounded, then stop the worker.</summary>
    internal static async Task CloudShutdownAsync(TimeSpan budget)
    {
        if (!_cloudMode) return;
        try
        {
            if (_cloudEngine != null && _cloudAccountId != null)
            {
                using var cts = new CancellationTokenSource(budget);
                var push = CloudPushOnceAsync(cts.Token);
                await Task.WhenAny(push, Task.Delay(budget)).ConfigureAwait(false);
            }
        }
        catch { }
        try { _cloudCts.Cancel(); } catch { }
    }

    /// <summary>Stats block for brain_stats in cloud mode.</summary>
    private static JObject CloudStats() => new()
    {
        ["mode"] = "cloud",
        ["account"] = _cloudAccountId == null ? null : ShortId(_cloudAccountId),
        ["online"] = _cloudOnline,
        ["status"] = _cloudStatus,
        ["cache"] = _vaultPath,
        ["lastPullUtc"] = _cloudLastPullUtc,
        ["lastPushUtc"] = _cloudLastPushUtc,
        ["pendingPush"] = Volatile.Read(ref _cloudPendingPushes) == 1,
    };

    /// <summary>One line for the initialize instructions, or empty outside cloud mode.</summary>
    private static string CloudInstructionsLine() => !_cloudMode ? "" :
        $"\n\nBRAINX CLOUD: these notes are the owner's cloud brain (cached on this machine; writes are sent back to the cloud). Status: {_cloudStatus}.";

    private static string ShortId(string id) => id.Length > 8 ? id[..8] : id;

    // An env token has no stored sign-in to say which account it belongs to.
    // Remember it by a hash of the token — never the token — so the right
    // cache is found again when the cloud is offline.
    private static string EnvTokenMapPath => Path.Combine(CloudCredentialStore.CacheRoot, "env-token-accounts.json");

    private static string TokenKey(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("brainx-env-token|" + token)))[..24].ToLowerInvariant();

    private static string? EnvTokenAccount(string token)
    {
        try
        {
            if (!File.Exists(EnvTokenMapPath)) return null;
            var o = JObject.Parse(File.ReadAllText(EnvTokenMapPath));
            return o[TokenKey(token)]?.ToString();
        }
        catch { return null; }
    }

    private static void RememberEnvTokenAccount(string token, string accountId)
    {
        try
        {
            Directory.CreateDirectory(CloudCredentialStore.CacheRoot);
            var o = File.Exists(EnvTokenMapPath) ? JObject.Parse(File.ReadAllText(EnvTokenMapPath)) : new JObject();
            o[TokenKey(token)] = accountId;
            File.WriteAllText(EnvTokenMapPath, o.ToString(Formatting.Indented), new UTF8Encoding(false));
        }
        catch { }
    }
}
