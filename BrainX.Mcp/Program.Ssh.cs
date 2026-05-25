using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using BrainX.Core.Services;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Owner-realm SSH tools.
//
// These tools let Claude dial whitelisted servers to grep logs / read
// config / check process status WITHOUT a per-call confirmation prompt,
// so the model can answer "is exim queued up?" or "what's in
// /var/log/php_errors.log?" in one tool turn instead of three round-trips
// through the human.
//
// Trust boundary: profiles + key paths live in <vault>/.obsidianx/
// ssh-profiles.json. The owner controls that file by hand. The MCP
// process reads it — never writes it from Claude — and the on-server
// authorized_keys for the brain's key SHOULD pin command="..." for true
// belt-and-suspenders. See the threat-model note in the brain:
// [[Join Brain v2 + Brain-as-SSH-Gateway — realm separation threat surface]].
//
// These tools are deliberately NOT exposed via BrainHub — peer brains
// cannot see or call them. Owner-only by construction.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    private static SshProfileStore? _sshStoreCached;
    private static SshExecutor? _sshExecutorCached;
    private static readonly object _sshInitLock = new();

    private static SshProfileStore SshStore
    {
        get
        {
            if (_sshStoreCached != null) return _sshStoreCached;
            lock (_sshInitLock)
            {
                _sshStoreCached ??= new SshProfileStore(_vaultPath);
                _sshStoreCached.EnsureTemplateExists();
                return _sshStoreCached;
            }
        }
    }

    private static SshExecutor SshExec
    {
        get
        {
            if (_sshExecutorCached != null) return _sshExecutorCached;
            lock (_sshInitLock)
            {
                _sshExecutorCached ??= new SshExecutor(_vaultPath);
                return _sshExecutorCached;
            }
        }
    }

    private static JToken SshProfilesList()
    {
        var profiles = SshStore.LoadAll();
        var arr = new JArray();
        foreach (var p in profiles)
        {
            arr.Add(new JObject
            {
                ["id"] = p.Id,
                ["host"] = p.Host,
                ["port"] = p.Port,
                ["user"] = p.User,
                ["description"] = p.Description,
                ["allow_patterns_count"] = p.AllowPatterns.Count,
                ["max_runtime_sec"] = p.MaxRuntimeSec,
                ["require_confirmation"] = p.RequireConfirmation,
                ["audit_to_brain"] = p.AuditToBrain
            });
        }
        return new JObject
        {
            ["count"] = profiles.Count,
            ["config_path"] = SshStore.ConfigPath,
            ["profiles"] = arr,
            ["hint"] = profiles.Count == 0
                ? "no profiles yet — edit " + SshStore.ConfigPath + " to add hosts"
                : "use one of these ids as profile_id when calling ssh_run / ssh_tail"
        };
    }

    private static JToken SshRun(JObject args)
    {
        var profileId = args["profile_id"]?.ToString();
        var command   = args["command"]?.ToString();
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("profile_id is required");
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("command is required");

        var profile = SshStore.GetById(profileId);
        if (profile == null)
        {
            return new JObject
            {
                ["allowed"] = false,
                ["error"] = $"unknown profile: {profileId}",
                ["hint"] = "call ssh_profiles_list to see available ids"
            };
        }

        var result = ExecAndAuditAsync(profile, command).GetAwaiter().GetResult();
        return ResultToJson(result, profile.Id, command);
    }

    private static JToken SshTail(JObject args)
    {
        var profileId = args["profile_id"]?.ToString();
        var path      = args["path"]?.ToString();
        var lines     = args["lines"]?.Value<int?>() ?? 200;
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("profile_id is required");
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("path is required");
        if (lines < 1) lines = 1;
        if (lines > 5000) lines = 5000;

        // ssh_tail enforces its OWN narrower contract on `path` independent
        // of the profile's CommandGuard rules: only POSIX-safe path chars.
        // Without this, a path like "/var/log/x; rm -rf /" would survive
        // an allow-pattern like ^tail -n \d+ /var/log/.*$ because the .*
        // happily matches the semicolon and everything after.
        if (!TailPathSafe.IsMatch(path))
        {
            return new JObject
            {
                ["allowed"] = false,
                ["error"] = "path contains characters not allowed for ssh_tail (only letters, digits, /, ., _, -, +, : are accepted). Use ssh_run with a profile-specific allow_pattern if you need anything else."
            };
        }

        var profile = SshStore.GetById(profileId);
        if (profile == null)
        {
            return new JObject
            {
                ["allowed"] = false,
                ["error"] = $"unknown profile: {profileId}"
            };
        }

        var command = $"tail -n {lines} {path}";
        var result = ExecAndAuditAsync(profile, command).GetAwaiter().GetResult();
        return ResultToJson(result, profile.Id, command);
    }

    /// <summary>
    /// Whitelist for ssh_tail's <c>path</c> argument. Letters, digits, and
    /// these literals only: <c>/ . _ - + :</c>. No spaces, no quotes, no
    /// shell metacharacters. Anything outside this set is rejected before
    /// string-interpolating into <c>tail -n N PATH</c>, so a malformed or
    /// hostile path can never reach the remote shell.
    /// </summary>
    private static readonly Regex TailPathSafe = new(
        @"^[/A-Za-z0-9._\-+:]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static async Task<SshExecResult> ExecAndAuditAsync(SshProfile profile, string command)
    {
        // Cap each call independently of the profile's MaxRuntimeSec so a
        // misconfigured profile (e.g. MaxRuntimeSec=600) can't pin the MCP
        // process for that long — 60s is the absolute ceiling here.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Min(profile.MaxRuntimeSec + 5, 60)));

        SshExecResult result;
        try
        {
            result = await SshExec.RunAsync(profile, command, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = SshExecResult.Failure("exec timed out");
        }
        catch (Exception ex)
        {
            result = SshExecResult.Failure($"exec exception: {ex.Message}");
        }

        if (profile.AuditToBrain)
        {
            // Host-key mismatch is a distinct event — possible MITM or an
            // unapproved server rekey. Tag it separately so the owner can
            // spot it in access-log.ndjson + the universe graph without
            // grepping error strings.
            var op = result.IsHostKeyMismatch ? "ssh_mitm"
                   : !result.Allowed         ? "ssh_denied"
                   : result.Success          ? "ssh_ok"
                                             : "ssh_fail";
            // Reuse the brain's access log — the universe graph already
            // pulses on this stream, so SSH calls light up the host alongside
            // note reads. Pseudo-node id "ssh:<profile_id>" keeps profiles
            // distinct without colliding with real note ids. For ssh_mitm
            // we ALSO include the fingerprint detail in the context so the
            // owner sees exactly what changed.
            var context = result.IsHostKeyMismatch
                ? $"{command} || {result.Error}"
                : command;
            LogAccess($"ssh:{profile.Id}", op, context);
        }

        return result;
    }

    private static JObject ResultToJson(SshExecResult r, string profileId, string command)
    {
        var obj = new JObject
        {
            ["profile_id"] = profileId,
            ["command"] = command,
            ["allowed"] = r.Allowed,
            ["success"] = r.Success,
            ["exit_code"] = r.ExitCode,
            ["matched_pattern"] = r.MatchedPattern
        };
        if (r.IsHostKeyMismatch)
            obj["host_key_mismatch"] = true;     // distinct flag — possible MITM, surfaces to Claude + user
        if (!string.IsNullOrEmpty(r.Error))
            obj["error"] = r.Error;
        if (!string.IsNullOrEmpty(r.Stdout))
            obj["stdout"] = r.Stdout;
        if (!string.IsNullOrEmpty(r.Stderr))
            obj["stderr"] = r.Stderr;
        return obj;
    }
}
