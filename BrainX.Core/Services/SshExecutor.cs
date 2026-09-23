using Renci.SshNet;
using Renci.SshNet.Common;

namespace BrainX.Core.Services;

/// <summary>
/// Open a one-shot SSH connection per request, run a single whitelisted
/// command, capture stdout/stderr/exitcode, close. No connection pooling
/// in this phase — each Claude diagnostic round-trips a fresh handshake,
/// which is fine for the low-throughput use case (humans reading logs).
///
/// The executor consults <see cref="CommandGuard"/> internally on every
/// call, so callers don't need a pre-validation step — but the guard is
/// the actual policy boundary; this class is just the transport. Adding
/// more rules later means editing the guard, not the executor.
///
/// Host key checking is strict: connections fail closed if the
/// presented host key doesn't match a known_hosts entry. The .obsidianx
/// dir keeps its own known_hosts (separate from the OS one) so the
/// brain's trust set is portable with the vault.
/// </summary>
public class SshExecutor
{
    /// <summary>
    /// Hard ceiling per stream (stdout, stderr). Anything beyond this is
    /// dropped with a trailing marker. Keeps a single huge log read (5GB
    /// catfile) from OOM'ing the MCP host or stuffing megabytes into the
    /// JSON-RPC frame the Claude client has to parse. Conservative — most
    /// diagnostic reads land well under 50 KB.
    /// </summary>
    public const int MaxOutputBytes = 200_000;

    private readonly string _knownHostsPath;
    private readonly object _knownHostsLock = new();

    public SshExecutor(string vaultPath)
    {
        var dir = Path.Combine(vaultPath, ".obsidianx");
        Directory.CreateDirectory(dir);
        _knownHostsPath = Path.Combine(dir, "ssh-known-hosts.txt");
    }

    public string KnownHostsPath => _knownHostsPath;

    /// <summary>
    /// Run one command, and END it at <paramref name="deadline"/> (default: the
    /// profile's max_runtime_sec).
    ///
    /// The old version wrapped the blocking <c>Connect()</c>/<c>Execute()</c> in
    /// <c>Task.Run(…, ct)</c>. A token passed to Task.Run is only checked before
    /// the delegate STARTS, so the caller's 60-second ceiling never fired: the
    /// call returned when the remote command did, up to max_runtime_sec (600 /
    /// 900 on the production profiles) — with the MCP's single stdio loop, and
    /// every other brain tool in that session, waiting behind it. Now the
    /// connect is <c>ConnectAsync(token)</c>, and the command is
    /// <c>ExecuteAsync(token)</c>, whose cancellation SIGNALS the remote
    /// process; it is followed by a SIGKILL and a disconnect.
    /// </summary>
    public async Task<SshExecResult> RunAsync(
        SshProfile profile,
        string command,
        CancellationToken ct = default,
        TimeSpan? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(command);

        var guard = CommandGuard.Validate(command, profile);
        if (!guard.Allowed)
        {
            return SshExecResult.Denied(
                $"command blocked by guard: {guard.Reason}" +
                (guard.MatchedPattern is null ? "" : $" (pattern: {guard.MatchedPattern})"));
        }

        var limit = deadline ?? TimeSpan.FromSeconds(Math.Clamp(profile.MaxRuntimeSec, 1, 3600));
        var connectLimit = TimeSpan.FromSeconds(Math.Clamp(limit.TotalSeconds, 1, 30));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        runCts.CancelAfter(limit);

        if (!File.Exists(profile.KeyPath))
            return SshExecResult.Failure($"key file not found: {profile.KeyPath}");

        PrivateKeyFile keyFile;
        try
        {
            keyFile = string.IsNullOrEmpty(profile.KeyPassphrase)
                ? new PrivateKeyFile(profile.KeyPath)
                : new PrivateKeyFile(profile.KeyPath, profile.KeyPassphrase);
        }
        catch (SshPassPhraseNullOrEmptyException)
        {
            return SshExecResult.Failure("private key is passphrase-protected but no passphrase was supplied");
        }
        catch (InvalidOperationException ex)
        {
            return SshExecResult.Failure($"key load failed: {ex.Message}");
        }

        var auth = new PrivateKeyAuthenticationMethod(profile.User, keyFile);
        var info = new ConnectionInfo(profile.Host, profile.Port, profile.User, auth)
        {
            Timeout = connectLimit
        };

        // Subscribe to HostKeyReceived so we can enforce known-hosts
        // matching ourselves — SSH.NET accepts everything by default.
        using var client = new SshClient(info);
        var hostKeyOk = true;
        var hostKeyError = "";
        var hostKeyMismatch = false;
        client.HostKeyReceived += (_, e) =>
        {
            var fingerprint = FormatFingerprint(e.HostKey, e.HostKeyName);
            if (!VerifyKnownHost(profile.Host, profile.Port, e.HostKeyName, fingerprint, out var detail))
            {
                hostKeyOk = false;
                hostKeyMismatch = true;     // explicit mismatch (not first-pin)
                hostKeyError = detail;
                e.CanTrust = false;
                return;
            }
            e.CanTrust = true;
        };

        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(runCts.Token);
            connectCts.CancelAfter(connectLimit);
            await client.ConnectAsync(connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return SshExecResult.TimedOutAfter(
                $"connect timed out after {connectLimit.TotalSeconds:0}s", clock.ElapsedMilliseconds);
        }
        catch (SshOperationTimeoutException)
        {
            return SshExecResult.TimedOutAfter(
                $"connect timed out after {connectLimit.TotalSeconds:0}s", clock.ElapsedMilliseconds);
        }
        catch (SshConnectionException ex)
        {
            return hostKeyMismatch
                ? SshExecResult.HostKeyMismatch(hostKeyError)
                : SshExecResult.Failure($"connect failed: {ex.Message}");
        }
        catch (SshAuthenticationException ex)
        {
            return SshExecResult.Failure($"auth failed: {ex.Message}");
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            return SshExecResult.Failure($"network error: {ex.Message}");
        }

        if (!hostKeyOk)
        {
            try { client.Disconnect(); } catch { /* swallow */ }
            return SshExecResult.HostKeyMismatch(hostKeyError);
        }

        try
        {
            using var cmd = client.CreateCommand(command);
            // A backstop only. The token is what ends the command, because
            // cancelling it signals the remote process — a CommandTimeout just
            // stops waiting for it.
            cmd.CommandTimeout = limit + TimeSpan.FromSeconds(15);

            try
            {
                await cmd.ExecuteAsync(runCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SshOperationTimeoutException)
            {
                // ExecuteAsync already sent SIGTERM on the token. A process that
                // traps it gets SIGKILL; a server that ignores signals gets the
                // session closed under it in `finally`.
                try { cmd.CancelAsync(forceKill: true, millisecondsTimeout: 1000); }
                catch { /* already finished, or the server ignores signals */ }
                return new SshExecResult
                {
                    Allowed = true,
                    Success = false,
                    TimedOut = true,
                    ExitCode = -1,
                    Error = $"timed out after {limit.TotalSeconds:0}s — the remote command was sent SIGTERM then SIGKILL and the session was closed",
                    Stdout = CapOutput(ReadWithin(() => cmd.Result, 1000)),
                    Stderr = CapOutput(ReadWithin(() => cmd.Error, 500)),
                    MatchedPattern = guard.MatchedPattern,
                    DurationMs = clock.ElapsedMilliseconds
                };
            }

            var exit = cmd.ExitStatus ?? -1;
            return new SshExecResult
            {
                Allowed = true,
                Success = exit == 0,
                ExitCode = exit,
                Stdout = CapOutput(cmd.Result ?? ""),
                Stderr = CapOutput(cmd.Error ?? ""),
                MatchedPattern = guard.MatchedPattern,
                DurationMs = clock.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return SshExecResult.Failure($"exec failed: {ex.Message}");
        }
        finally
        {
            try { client.Disconnect(); } catch { /* swallow */ }
        }
    }

    /// <summary>
    /// Partial output of a command that was cancelled — worth returning (the
    /// last lines before a hang are usually the answer), never worth waiting
    /// for: the read is abandoned if the stream does not end promptly.
    /// </summary>
    private static string ReadWithin(Func<string?> read, int milliseconds)
    {
        try
        {
            var t = Task.Run(read);
            return t.Wait(milliseconds) ? t.Result ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Check ssh-known-hosts.txt for a matching fingerprint. First-time
    /// connect to a host appends a TOFU entry — the audit log captures
    /// this so the owner can review (and revoke by editing the file).
    /// Lock-protected because two concurrent connects to the same fresh
    /// host could otherwise both write a pin line.
    /// </summary>
    private bool VerifyKnownHost(string host, int port, string keyType, string fingerprint, out string detail)
    {
        detail = "";
        var entryKey = $"[{host}]:{port} {keyType}";

        lock (_knownHostsLock)
        {
            if (!File.Exists(_knownHostsPath))
            {
                File.WriteAllText(_knownHostsPath, $"{entryKey} {fingerprint}\n");
                return true;
            }

            foreach (var raw in File.ReadAllLines(_knownHostsPath))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (!line.StartsWith(entryKey, StringComparison.OrdinalIgnoreCase)) continue;

                var stored = line[entryKey.Length..].Trim();
                if (string.Equals(stored, fingerprint, StringComparison.OrdinalIgnoreCase))
                    return true;

                detail = $"fingerprint mismatch for {entryKey} — stored={stored} presented={fingerprint}";
                return false;
            }

            // Host not seen before — TOFU pin.
            File.AppendAllText(_knownHostsPath, $"{entryKey} {fingerprint}\n");
            return true;
        }
    }

    private static string FormatFingerprint(byte[] hostKey, string keyType)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(hostKey);
        return $"SHA256:{Convert.ToBase64String(hash).TrimEnd('=')}";
    }

    /// <summary>
    /// Truncate a single output stream to <see cref="MaxOutputBytes"/> chars
    /// and append a one-line marker telling the reader how many chars were
    /// dropped. Returning a JSON-friendly capped string means a runaway log
    /// dump can't OOM the MCP host or balloon the JSON-RPC frame.
    /// </summary>
    private static string CapOutput(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= MaxOutputBytes) return s;
        var dropped = s.Length - MaxOutputBytes;
        return s[..MaxOutputBytes] + $"\n[truncated — {dropped} additional chars omitted; raise the profile's allow_patterns scope or read fewer lines]";
    }
}

public class SshExecResult
{
    /// <summary>True if the command guard accepted the request (regardless of whether the remote command succeeded).</summary>
    public bool Allowed { get; set; }

    /// <summary>True if Allowed AND remote exit code is 0.</summary>
    public bool Success { get; set; }

    /// <summary>
    /// True when the connection failed specifically because the server's
    /// host key didn't match the pinned fingerprint in known-hosts. This
    /// signals a possible MITM (or a legitimate host rekey the owner
    /// hasn't approved yet). Callers should audit this as a distinct
    /// event, not a generic transport failure.
    /// </summary>
    public bool IsHostKeyMismatch { get; set; }

    public int ExitCode { get; set; } = -1;
    public string Stdout { get; set; } = string.Empty;
    public string Stderr { get; set; } = string.Empty;

    /// <summary>Set when the guard denied OR the transport failed before exec. Empty on a successful exec.</summary>
    public string? Error { get; set; }

    /// <summary>The allow-pattern that matched, for audit-log clarity.</summary>
    public string? MatchedPattern { get; set; }

    /// <summary>True when the deadline ended the call (connect or command).</summary>
    public bool TimedOut { get; set; }

    /// <summary>Wall time of the call, when it got as far as connecting.</summary>
    public long? DurationMs { get; set; }

    public static SshExecResult TimedOutAfter(string error, long durationMs) => new()
    {
        Allowed = true,
        Success = false,
        TimedOut = true,
        Error = error,
        DurationMs = durationMs
    };

    public static SshExecResult Denied(string error) => new()
    {
        Allowed = false,
        Success = false,
        Error = error
    };

    public static SshExecResult Failure(string error) => new()
    {
        Allowed = true,
        Success = false,
        Error = error
    };

    public static SshExecResult HostKeyMismatch(string error) => new()
    {
        Allowed = true,
        Success = false,
        IsHostKeyMismatch = true,
        Error = $"host key check failed: {error}"
    };
}
