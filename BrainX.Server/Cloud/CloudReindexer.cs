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
    /// <summary>Re-index one account. True on success. Must honour <paramref name="ct"/> (shutdown).</summary>
    public delegate Task<bool> Runner(string accountId, string vaultDir, string accountDir, CancellationToken ct);

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
        public string Vault = "";
        public string Dir = "";
        public int Completed;
        public int Succeeded;
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
        lock (st.Gate)
        {
            if (_disposed) return;
            if (st.Running) { st.Pending = true; return; }
            st.Running = true;
            st.Pending = false;
            vault = st.Vault;
            dir = st.Dir;
        }

        var ok = false;
        try
        {
            await _global.WaitAsync(_cts.Token).ConfigureAwait(false);
            try { ok = await _runner!(accountId, vault, dir, _cts.Token).ConfigureAwait(false); }
            finally { _global.Release(); }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Console.WriteLine($"[cloud] reindex {CloudIds.ShortId(accountId)} failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            lock (st.Gate)
            {
                st.Running = false;
                st.Completed++;
                if (ok) st.Succeeded++;
                if (st.Pending && !_disposed)
                {
                    st.Pending = false;
                    try { st.Timer?.Change(_debounce, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
                }
            }
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

    public static async Task<bool> RunAsync(string mcpExe, string accountId, string vaultDir, string accountDir, CancellationToken ct)
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
            Console.WriteLine($"[cloud] reindex {CloudIds.ShortId(accountId)} "
                              + (ct.IsCancellationRequested ? "cancelled (shutdown)" : $"killed after {MaxRunTime.TotalMinutes:0} min"));
            return false;
        }

        var err = await stderr.ConfigureAwait(false);
        await stdout.ConfigureAwait(false);
        if (proc.ExitCode != 0)
        {
            Console.WriteLine($"[cloud] reindex {CloudIds.ShortId(accountId)} failed (exit {proc.ExitCode}): {Tail(err)}");
            return false;
        }
        if (!File.Exists(shadow))
        {
            Console.WriteLine($"[cloud] reindex {CloudIds.ShortId(accountId)} produced no index (is brainx-mcp too old for `export --out`?)");
            return false;
        }

        var target = Path.Combine(vaultDir, ".obsidianx", "brain-export.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await CloudVaults.MoveWithRetryAsync(shadow, target).ConfigureAwait(false);
        Console.WriteLine($"[cloud] reindexed {CloudIds.ShortId(accountId)} in {sw.Elapsed.TotalSeconds:0.0}s");
        return true;
    }

    private static string Tail(string s)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= 300 ? s : "…" + s[^300..];
    }
}
