using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace BrainX.Server.Cloud;

/// <summary>
/// Rebuilds an account's brain-export.json after its notes change, debounced:
/// a sync uploads in batches, and re-indexing after every batch would index
/// the same vault dozens of times. Each write pushes the run back by the
/// debounce; the run happens once the account has been quiet that long.
///
/// Two invariants:
///   • NEVER TWO EXPORTS OF ONE ACCOUNT AT ONCE. A write that lands while an
///     export runs does not start a second one — it marks the account pending,
///     and exactly one more (debounced) run follows the current one, so the
///     index always ends up covering the last write.
///   • BOUNDED across accounts: at most <c>maxConcurrent</c> exports run node-wide.
/// </summary>
public sealed class CloudReindexer : IDisposable
{
    /// <summary>Re-index one account. Must honour <paramref name="ct"/> (shutdown,
    /// or the account being deleted).</summary>
    public delegate Task<ReindexOutcome> Runner(string accountId, string vaultDir, string accountDir, CancellationToken ct);

    private readonly TimeSpan _debounce;
    private readonly Runner? _runner;
    private readonly SemaphoreSlim _global;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    private sealed class State
    {
        public readonly object Gate = new();
        public Timer? Timer;
        public bool Running;
        public bool Pending;
        public bool Forgotten;
        public string Vault = "";
        public string Dir = "";
        public int Completed;
        public int Succeeded;
        public CancellationTokenSource? RunCts;
        public ReindexResult? Last;
    }

    public CloudReindexer(TimeSpan debounce, int maxConcurrent, Runner? runner)
    {
        _debounce = debounce;
        _runner = runner;
        _global = new SemaphoreSlim(Math.Max(1, maxConcurrent));
    }

    /// <summary>False when there is nothing to run the export with (no brainx-mcp).</summary>
    public bool Available => _runner != null;

    /// <summary>(Re)arm the account's timer. <paramref name="delay"/> overrides the
    /// debounce (an explicit /reindex asks for zero).</summary>
    public bool Schedule(string accountId, string vaultDir, string accountDir, TimeSpan? delay = null)
    {
        if (_runner is null || _disposed) return false;
        var st = _states.GetOrAdd(accountId, _ => new State());
        lock (st.Gate)
        {
            st.Vault = vaultDir;
            st.Dir = accountDir;
            if (st.Running)
            {
                st.Pending = true;   // one more run after this one, never a second concurrent one
                return true;
            }
            st.Timer ??= new Timer(_ => _ = FireAsync(accountId, st), null, Timeout.Infinite, Timeout.Infinite);
            st.Timer.Change(delay ?? _debounce, Timeout.InfiniteTimeSpan);
        }
        return true;
    }

    private async Task FireAsync(string accountId, State st)
    {
        string vault, dir;
        CancellationTokenSource runCts;
        lock (st.Gate)
        {
            if (_disposed || st.Forgotten) return;
            if (st.Running) { st.Pending = true; return; }
            st.Running = true;
            st.Pending = false;
            vault = st.Vault;
            dir = st.Dir;
            runCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            st.RunCts = runCts;
        }

        var outcome = new ReindexOutcome(false, "did not run");
        try
        {
            await _global.WaitAsync(runCts.Token).ConfigureAwait(false);
            try { outcome = await _runner!(accountId, vault, dir, runCts.Token).ConfigureAwait(false); }
            finally { _global.Release(); }
        }
        catch (OperationCanceledException)
        {
            outcome = new ReindexOutcome(false, "cancelled");
        }
        catch (Exception ex)
        {
            // Type only in the log: an IO message carries the vault path.
            outcome = new ReindexOutcome(false, $"{ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"[cloud] reindex {CloudIds.ShortId(accountId)} failed: {ex.GetType().Name}");
        }
        finally
        {
            lock (st.Gate)
            {
                st.Running = false;
                st.RunCts = null;
                st.Completed++;
                if (outcome.Ok) st.Succeeded++;
                st.Last = new ReindexResult(DateTimeOffset.UtcNow, outcome.Ok, outcome.Message);
                if (st.Pending && !_disposed && !st.Forgotten)
                {
                    st.Pending = false;
                    try { st.Timer?.Change(_debounce, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
                }
            }
            runCts.Dispose();
        }
    }

    /// <summary>The account's most recent re-index, or null if none ran since start.</summary>
    public ReindexResult? LastResult(string accountId)
    {
        if (!_states.TryGetValue(accountId, out var st)) return null;
        lock (st.Gate) return st.Last;
    }

    /// <summary>
    /// Stop everything for one account (admin delete): no timer, no pending run,
    /// the running export cancelled (its process is killed). Waits up to
    /// <paramref name="wait"/> for that run to finish letting go of the vault.
    /// </summary>
    public async Task ForgetAsync(string accountId, TimeSpan wait)
    {
        if (!_states.TryRemove(accountId, out var st)) return;
        lock (st.Gate)
        {
            st.Forgotten = true;
            st.Pending = false;
            st.Timer?.Dispose();
            st.Timer = null;
            try { st.RunCts?.Cancel(); } catch (ObjectDisposedException) { }
        }
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            lock (st.Gate) if (!st.Running) return;
            await Task.Delay(50).ConfigureAwait(false);
        }
    }

    /// <summary>Completed runs for an account (success or not) — for tests and diagnostics.</summary>
    public int CompletedRuns(string accountId)
    {
        if (!_states.TryGetValue(accountId, out var st)) return 0;
        lock (st.Gate) return st.Completed;
    }

    public bool IsIdle(string accountId)
    {
        if (!_states.TryGetValue(accountId, out var st)) return true;
        lock (st.Gate) return !st.Running && !st.Pending;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        foreach (var st in _states.Values)
            lock (st.Gate) { st.Timer?.Dispose(); st.Timer = null; }
    }
}

/// <summary>
/// The production re-index: <c>brainx-mcp export --vault &lt;accountVault&gt; --out
/// &lt;accountDir&gt;/index --quiet</c>, then the new brain-export.json is renamed
/// into <c>&lt;vault&gt;/.obsidianx/</c>.
///
/// Why the shadow form (<c>--out</c>) rather than a plain export: a plain export
/// also splices a managed section into <c>&lt;vault&gt;/CLAUDE.md</c> (creating it
/// when missing). In a cloud vault that is a note the customer never uploaded —
/// it would show up in every manifest and every client's pull, and for a
/// customer who DID upload a root CLAUDE.md the server would keep rewriting it
/// under their client (and race their uploads of it). The shadow export writes
/// only the index; the rename puts it exactly where the MCP child reads it.
/// </summary>
public static class CloudExport
{
    public static readonly TimeSpan MaxRunTime = TimeSpan.FromMinutes(10);

    public static CloudReindexer.Runner Runner(string mcpExe)
        => (accountId, vault, dir, ct) => RunAsync(mcpExe, accountId, vault, dir, ct);

    /// <summary>
    /// The outcome's message is for the owner's admin view (local-only); the
    /// node log gets only the exit code — brainx-mcp's stderr can name note
    /// files, and the log never carries customer content.
    /// </summary>
    public static async Task<ReindexOutcome> RunAsync(string mcpExe, string accountId, string vaultDir, string accountDir, CancellationToken ct)
    {
        // brainx-mcp falls back to a default vault when the one it is handed
        // does not exist — so it must exist before the process starts.
        Directory.CreateDirectory(vaultDir);
        var outDir = Path.Combine(accountDir, "index");
        var shadow = Path.Combine(outDir, ".obsidianx", "brain-export.json");
        try { if (File.Exists(shadow)) File.Delete(shadow); } catch { /* the export overwrites it */ }

        var psi = new ProcessStartInfo(mcpExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { "export", "--vault", vaultDir, "--out", outDir, "--quiet" })
            psi.ArgumentList.Add(a);
        psi.Environment["BRAINX_VAULT"] = vaultDir;
        // Nothing outside the account's vault is touched (no Claude/Codex rule
        // installers, no desktop side effects) — a customer's export must never
        // write into the node owner's own tooling.
        psi.Environment["BRAINX_HEADLESS"] = "1";
        psi.Environment["BRAINX_SANDBOX"] = "1";

        var sw = Stopwatch.StartNew();
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start brainx-mcp export");
        var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(MaxRunTime);
        try
        {
            await proc.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            var why = ct.IsCancellationRequested ? "cancelled" : $"killed after {MaxRunTime.TotalMinutes:0} min";
            Console.WriteLine($"[cloud] reindex {CloudIds.ShortId(accountId)} {why}");
            return new ReindexOutcome(false, why);
        }

        var err = await stderr.ConfigureAwait(false);
        await stdout.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            Console.WriteLine($"[cloud] reindex {CloudIds.ShortId(accountId)} failed (brainx-mcp exit {proc.ExitCode})");
            return new ReindexOutcome(false, $"brainx-mcp export exited {proc.ExitCode}: {Tail(err)}");
        }
        if (!File.Exists(shadow))
        {
            Console.WriteLine($"[cloud] reindex {CloudIds.ShortId(accountId)} produced no index");
            return new ReindexOutcome(false, "brainx-mcp export produced no index (too old for `export --out`?)");
        }

        var target = Path.Combine(vaultDir, ".obsidianx", "brain-export.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await CloudVaults.MoveWithRetryAsync(shadow, target).ConfigureAwait(false);
        var took = sw.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine($"[cloud] reindexed {CloudIds.ShortId(accountId)} in {took}s");
        return new ReindexOutcome(true, $"reindexed in {took}s");
    }

    private static string Tail(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 300 ? s : "…" + s[^300..];
    }
}

/// <summary>What one re-index run reports. A bare bool converts, for runners
/// that have nothing more to say.</summary>
public readonly record struct ReindexOutcome(bool Ok, string Message)
{
    public static implicit operator ReindexOutcome(bool ok) => new(ok, ok ? "ok" : "failed");
}

/// <summary>An account's most recent re-index, for the admin detail view.</summary>
public sealed record ReindexResult(DateTimeOffset Utc, bool Ok, string Message);
