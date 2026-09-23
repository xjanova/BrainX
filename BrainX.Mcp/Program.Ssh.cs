using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BrainX.Core.Services;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Owner-realm SSH tools.
//
// These tools let an agent dial the owner's servers to grep logs, read config
// and run the owner's own deploy steps in one tool turn instead of three
// round-trips through the human.
//
// Trust boundary: profiles + key paths live in <vault>/.obsidianx/
// ssh-profiles.json. The owner controls that file by hand. The MCP process
// reads it — never writes it — and the on-server authorized_keys for the
// brain's key SHOULD pin command="..." for true belt-and-suspenders. See the
// threat-model note in the brain:
// [[Join Brain v2 + Brain-as-SSH-Gateway — realm separation threat surface]].
//
// THE GATE (2026-09-23). Profiles can allow `^.+`, and their deny regexes
// were measured leaking `rm -rf /`, `dd of=/dev/sda`, `DROP DATABASE` and
// `curl | sh`. So every command now passes, in order:
//   1. the profile's own allow/deny (CommandGuard) — a refusal is final;
//   2. a check for commands that never exit (tail -f, watch);
//   3. the built-in destructive classifier (SshCommandRisk), plus the
//      profile's require_confirmation — either sends the command to the
//      OWNER for approval (SshApprovalStore + `brainx-mcp ssh-approve`),
//      and only an unspent approval for the identical string runs it.
// Audit rows are redacted and live in their own rotating file
// (.obsidianx/ssh-audit.ndjson); the access log keeps only a short pulse row
// for the dashboard feed and the universe.
//
// These tools are deliberately NOT exposed via BrainHub — peer brains cannot
// see or call them. Owner-only by construction.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    private static SshProfileStore? _sshStoreCached;
    private static SshExecutor? _sshExecutorCached;
    private static SshApprovalStore? _sshApprovalsCached;
    private static readonly object _sshInitLock = new();

    /// <summary>
    /// The longest one ssh_run may hold this session's stdio loop. The
    /// profile's max_runtime_sec applies under it; BRAINX_SSH_MAX_RUNTIME_SEC
    /// lowers (or raises) the ceiling for every profile at once.
    /// </summary>
    private static int SshMaxRuntimeCeilingSec =>
        int.TryParse(Environment.GetEnvironmentVariable("BRAINX_SSH_MAX_RUNTIME_SEC"), out var s) && s > 0 ? s : 900;

    private const long SshAuditMaxBytes = 2 * 1024 * 1024;
    private const int SshAuditKeepRotated = 12;

    private static string SshAuditPath => Path.Combine(_vaultPath, ".obsidianx", "ssh-audit.ndjson");

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

    private static SshApprovalStore SshApprovals
    {
        get
        {
            if (_sshApprovalsCached != null) return _sshApprovalsCached;
            lock (_sshInitLock) return _sshApprovalsCached ??= new SshApprovalStore();
        }
    }

    private static JToken SshProfilesList()
    {
        var profiles = SshStore.LoadAll();
        var arr = new JArray();
        foreach (var p in profiles)
        {
            var o = new JObject
            {
                ["id"] = p.Id,
                ["host"] = p.Host,
                ["port"] = p.Port,
                ["user"] = p.User,
                ["description"] = p.Description,
                ["allow_patterns_count"] = p.AllowPatterns.Count,
                ["max_runtime_sec"] = Math.Min(p.MaxRuntimeSec, SshMaxRuntimeCeilingSec),
                ["require_confirmation"] = p.RequireConfirmation,
                ["audit_to_brain"] = p.AuditToBrain
            };
            var findings = SshProfileLint.Lint(p);
            if (findings.Count > 0)
                o["warnings"] = new JArray(findings.Select(f => $"[{f.Level}] {f.Message}"));
            arr.Add(o);
        }
        return new JObject
        {
            ["count"] = profiles.Count,
            ["config_path"] = SshStore.ConfigPath,
            ["profiles"] = arr,
            ["confirmation"] = "Destructive commands (recursive rm outside /tmp, DROP/TRUNCATE, dd to a disk, curl|sh, " +
                               "service stop, reboot, git clean -f, …) need the OWNER's approval on every profile; " +
                               "require_confirmation:true makes every command on that profile need it. ssh_run answers " +
                               "needs_confirmation with a confirm_id — follow its how_to_approve.",
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

        return GateAndRun(profile, command, args["confirm_id"]?.ToString()?.Trim());
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

        // ssh_tail enforces its OWN narrower contract on `path` independent of
        // the profile's CommandGuard rules: POSIX path characters only, and
        // never a leading '-'. Without the first, "/var/log/x; rm -rf /"
        // survives an allow-pattern like ^tail -n \d+ /var/log/.*$; without
        // the second, path "-f" turned the call into `tail -n 200 -f`, which
        // never returns.
        if (!SshCommandRisk.IsSafeTailPath(path))
        {
            return new JObject
            {
                ["allowed"] = false,
                ["error"] = "path must be a file path: letters, digits, / . _ - + : only, and it may not start with '-'. " +
                            "Use ssh_run with a profile-specific allow_pattern if you need anything else."
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

        return GateAndRun(profile, $"tail -n {lines} {path}", args["confirm_id"]?.ToString()?.Trim());
    }

    /// <summary>
    /// Every ssh_run / ssh_tail passes through here — see THE GATE at the top
    /// of this file for the order and why it is that order.
    /// </summary>
    private static JToken GateAndRun(SshProfile profile, string command, string? confirmId)
    {
        // 1. The profile's own policy is final. Offering the owner an approval
        //    for something their profile refuses would turn the gate into a
        //    way around the profile.
        var guard = CommandGuard.Validate(command, profile);
        if (!guard.Allowed)
        {
            var denied = SshExecResult.Denied(
                $"command blocked by guard: {guard.Reason}" +
                (guard.MatchedPattern is null ? "" : $" (pattern: {guard.MatchedPattern})"));
            AuditSsh(profile, "ssh_denied", command, denied);
            return ResultToJson(denied, profile.Id, command);
        }

        // 2. A command that never ends would hold this session until the
        //    deadline, and nobody reads a follow through a request/response pipe.
        var hang = SshCommandRisk.NonTerminatingReason(command);
        if (hang != null)
        {
            var refused = SshExecResult.Denied(hang);
            AuditSsh(profile, "ssh_denied", command, refused);
            return ResultToJson(refused, profile.Id, command);
        }

        // 3. The owner's approval, when the command or the profile needs it.
        var hits = SshCommandRisk.Classify(command);
        string? spent = null;
        if (profile.RequireConfirmation || hits.Count > 0)
        {
            if (string.IsNullOrEmpty(confirmId))
                return RequestSshApproval(profile, command, hits, note: null);

            var (state, rec) = SshApprovals.TryConsume(confirmId, profile.Id, command);
            switch (state)
            {
                case SshApprovalState.Consumed:
                    spent = confirmId;
                    break;
                case SshApprovalState.Pending:
                    return ApprovalJson(profile, command, hits, rec!,
                        note: "The owner has not approved this yet. Do not retry in a loop — ask them to run approve_command, then call again.");
                case SshApprovalState.Expired:
                    return RequestSshApproval(profile, command, hits,
                        note: $"confirm_id {confirmId} expired before it was used — this is a fresh request.");
                case SshApprovalState.NotFound:
                    return RequestSshApproval(profile, command, hits,
                        note: $"confirm_id {confirmId} does not exist (mistyped, or swept after 24h) — this is a fresh request.");
                case SshApprovalState.Denied:
                    AuditSsh(profile, "ssh_denied", command, SshExecResult.Denied("owner denied"), confirmId, hits);
                    return new JObject
                    {
                        ["profile_id"] = profile.Id,
                        ["command"] = command,
                        ["allowed"] = false,
                        ["owner_denied"] = true,
                        ["confirm_id"] = confirmId,
                        ["error"] = "The owner DENIED this command. Do not retry it or a variant of it — ask the owner what they want instead."
                    };
                case SshApprovalState.Mismatch:
                    return new JObject
                    {
                        ["profile_id"] = profile.Id,
                        ["command"] = command,
                        ["allowed"] = false,
                        ["error"] = $"confirm_id {confirmId} was approved for a different command or profile. An approval covers ONE exact " +
                                    "command string — resend the approved command unchanged, or call without confirm_id to request approval for this one."
                    };
                case SshApprovalState.AlreadyUsed:
                    return new JObject
                    {
                        ["profile_id"] = profile.Id,
                        ["command"] = command,
                        ["allowed"] = false,
                        ["error"] = $"confirm_id {confirmId} was already used — each approval runs its command once. Call without confirm_id to request a new one."
                    };
                default: // Tampered
                    AuditSsh(profile, "ssh_confirm_tampered", command, null, confirmId, hits);
                    return new JObject
                    {
                        ["profile_id"] = profile.Id,
                        ["command"] = command,
                        ["allowed"] = false,
                        ["error"] = $"The approval record for {confirmId} failed its integrity check — it was not written by `brainx-mcp ssh-approve`. " +
                                    "Nothing ran. Tell the owner, and request approval again."
                    };
            }
        }

        var result = ExecAndAuditAsync(profile, command, spent, hits).GetAwaiter().GetResult();
        var json = ResultToJson(result, profile.Id, command);
        if (spent != null) json["confirm_id"] = spent;
        return json;
    }

    private static JObject RequestSshApproval(SshProfile profile, string command,
        IReadOnlyList<SshCommandRisk.Hit> hits, string? note)
    {
        var (rec, reused) = SshApprovals.RequestOrReuse(profile, command, hits, BusIdentity(), _vaultPath);
        if (!reused) AuditSsh(profile, "ssh_confirm_requested", command, null, rec.Id, hits);
        return ApprovalJson(profile, command, hits, rec, note);
    }

    private static JObject ApprovalJson(SshProfile profile, string command,
        IReadOnlyList<SshCommandRisk.Hit> hits, SshApprovalRecord rec, string? note)
    {
        var o = new JObject
        {
            ["profile_id"] = profile.Id,
            ["command"] = command,
            ["allowed"] = false,
            ["needs_confirmation"] = true,
            ["confirm_id"] = rec.Id,
            ["expires_at"] = rec.ExpiresAt.ToString("O"),
            ["reasons"] = hits.Count > 0
                ? new JArray(hits.Select(h => new JObject
                  {
                      ["rule"] = h.Rule,
                      ["reason"] = h.Reason,
                      ["catastrophic"] = h.Catastrophic,
                      ["at"] = h.Excerpt
                  }))
                : new JArray(new JObject
                  {
                      ["rule"] = "profile-requires-confirmation",
                      ["reason"] = $"profile '{profile.Id}' sets require_confirmation: every command needs the owner's approval"
                  }),
            ["approve_command"] = SshApproveCommandFor(rec.Id),
            ["how_to_approve"] =
                "STOP — do not retry, split or rephrase the command. Show the owner the exact `command`, the `reasons`, and " +
                "`approve_command`. The owner approves by running approve_command in THEIR OWN terminal (it shows the command " +
                "and asks them to type yes; it refuses to run from an agent's shell). When they say it is approved, call this " +
                "tool again with the same profile_id, the identical command and confirm_id. Nobody at the keyboard (headless " +
                "run)? Call agent_ask_user with the command and approve_command, then stop."
        };
        if (rec.Catastrophic) o["catastrophic"] = true;
        if (note != null) o["note"] = note;
        return o;
    }

    /// <summary>
    /// The command the owner pastes (or clicks Run on). Forward slashes and no
    /// quotes when the path allows it, so the same line works in PowerShell,
    /// cmd and Git Bash — the three shells this owner's terminals open.
    /// </summary>
    private static string SshApproveCommandFor(string id)
    {
        var exe = Environment.ProcessPath ?? "brainx-mcp";
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var dll = typeof(Program).Assembly.Location;
            return $"dotnet \"{dll}\" ssh-approve {id}";
        }
        return exe.Contains(' ')
            ? $"& \"{exe}\" ssh-approve {id}"
            : $"{exe.Replace('\\', '/')} ssh-approve {id}";
    }

    private static async Task<SshExecResult> ExecAndAuditAsync(SshProfile profile, string command,
        string? confirmId, IReadOnlyList<SshCommandRisk.Hit> hits)
    {
        // The profile's max_runtime_sec, under a ceiling — and now actually
        // enforced: SshExecutor signals the remote command and closes the
        // session at this deadline instead of waiting it out.
        var deadline = TimeSpan.FromSeconds(Math.Clamp(profile.MaxRuntimeSec, 1, SshMaxRuntimeCeilingSec));

        SshExecResult result;
        try
        {
            result = await SshExec.RunAsync(profile, command, CancellationToken.None, deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = SshExecResult.Failure("exec timed out");
        }
        catch (Exception ex)
        {
            result = SshExecResult.Failure($"exec exception: {ex.Message}");
        }

        // Host-key mismatch is a distinct event — possible MITM or an
        // unapproved server rekey — so it gets its own op.
        var op = result.IsHostKeyMismatch ? "ssh_mitm"
               : !result.Allowed         ? "ssh_denied"
               : result.Success          ? "ssh_ok"
                                         : "ssh_fail";
        AuditSsh(profile, op, command, result, confirmId, hits);
        return result;
    }

    /// <summary>
    /// One SSH event, twice: the full (redacted) row in ssh-audit.ndjson, and a
    /// short pulse in access-log.ndjson for the dashboard feed and the universe.
    /// The pulse is ordinary telemetry — it ages out like any impression — so
    /// SSH traffic can never again pin the access log above its cap.
    /// </summary>
    private static void AuditSsh(SshProfile profile, string op, string command, SshExecResult? result = null,
        string? confirmId = null, IReadOnlyList<SshCommandRisk.Hit>? hits = null, string? agent = null)
    {
        if (!profile.AuditToBrain) return;
        var redacted = SecretRedactor.Redact(command);
        var row = new JObject
        {
            ["ts"] = DateTime.UtcNow.ToString("O"),
            ["op"] = op,
            ["profile_id"] = profile.Id,
            ["agent"] = agent ?? BusIdentity(),
            ["command"] = redacted
        };
        if (result != null)
        {
            row["allowed"] = result.Allowed;
            row["exit_code"] = result.ExitCode;
            if (result.TimedOut) row["timed_out"] = true;
            if (result.DurationMs is long ms) row["duration_ms"] = ms;
            if (!string.IsNullOrEmpty(result.MatchedPattern)) row["matched_pattern"] = result.MatchedPattern;
            if (!string.IsNullOrEmpty(result.Error)) row["error"] = SecretRedactor.Redact(result.Error);
        }
        if (confirmId != null) row["confirm_id"] = confirmId;
        if (hits is { Count: > 0 }) row["risk"] = new JArray(hits.Select(h => h.Rule).Distinct());

        AppendSshAudit(row);

        var pulse = $"{profile.Id} · {(redacted.Length > 100 ? redacted[..100] + "…" : redacted)}";
        if (op == "ssh_mitm") pulse = $"{profile.Id} · HOST KEY MISMATCH";
        LogAccess($"ssh:{profile.Id}", op, pulse, pulse: true);
    }

    private static void AppendSshAudit(JObject row)
    {
        var line = row.ToString(Formatting.None);
        var m = AcquireVaultLock(2000, "sshaudit");
        try
        {
            RetryOnIo(() => NdjsonLog.AppendRotating(SshAuditPath, line, SshAuditMaxBytes, SshAuditKeepRotated));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { ReleaseVaultLock(m); }
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
        if (r.TimedOut)
            obj["timed_out"] = true;
        if (r.DurationMs is long ms)
            obj["duration_ms"] = ms;
        if (!string.IsNullOrEmpty(r.Error))
            obj["error"] = r.Error;
        if (!string.IsNullOrEmpty(r.Stdout))
            obj["stdout"] = r.Stdout;
        if (!string.IsNullOrEmpty(r.Stderr))
            obj["stderr"] = r.Stderr;
        return obj;
    }

    // ═════════════════════════════════════════════════════════════════════
    //   CLI — the owner's side of the gate
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>brainx-mcp ssh-approve &lt;confirm_id&gt; [--deny]</c> ·
    /// <c>brainx-mcp ssh-approve --list</c>
    ///
    /// Approving needs a human at a terminal: a redirected stdin — every agent
    /// shell, every pipe, every `echo yes |` — is refused before anything is
    /// read. Denying is allowed from anywhere; a denial only ever costs the
    /// agent a new request.
    /// </summary>
    internal static int SshApproveCli(string[] args)
    {
        try { Console.InputEncoding = new UTF8Encoding(false); } catch { }
        var store = new SshApprovalStore();

        if (args.Any(a => a is "--list" or "-l" or "list"))
        {
            var pending = store.ListPending();
            if (pending.Count == 0) { Console.WriteLine("ไม่มีคำขออนุมัติที่รออยู่ (no pending SSH approvals)"); return 0; }
            foreach (var p in pending)
            {
                Console.WriteLine($"{p.Id}  {p.ProfileId}  ขอโดย {p.RequestedBy}  เหลือ {Math.Max(0, (p.ExpiresAt - DateTime.UtcNow).TotalMinutes):0} นาที");
                Console.WriteLine($"    {SecretRedactor.Redact(p.Command ?? p.CommandDisplay)}");
            }
            return 0;
        }

        var id = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (id == null || args.Any(a => a is "-h" or "--help" or "help"))
        {
            Console.WriteLine("Usage: brainx-mcp ssh-approve <confirm_id> [--deny]");
            Console.WriteLine("       brainx-mcp ssh-approve --list");
            return id == null ? 2 : 0;
        }

        var rec = store.Load(id);
        if (rec == null) { Console.Error.WriteLine($"ไม่พบคำขอ {id} (no such request — expired, used, or mistyped)"); return 1; }

        if (!string.IsNullOrEmpty(rec.Vault) && Directory.Exists(rec.Vault)) _vaultPath = rec.Vault;
        var profile = new SshProfile { Id = rec.ProfileId, AuditToBrain = true };

        if (args.Any(a => a is "--deny" or "--no"))
        {
            var (okDeny, whyDeny) = store.Deny(id, "owner-cli");
            if (okDeny) AuditSsh(profile, "ssh_confirm_denied", rec.CommandDisplay, null, id, null, "owner");
            Console.WriteLine(okDeny ? $"❌ ไม่อนุมัติ {id} แล้ว" : $"ทำไม่ได้: {whyDeny}");
            return okDeny ? 0 : 1;
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("ต้องอนุมัติจาก terminal ที่คนพิมพ์เองเท่านั้น — this command refuses to approve from a script, a pipe, or an agent's shell.");
            Console.Error.WriteLine("Run it yourself in a terminal window (the Run button on the command in chat does that).");
            return 3;
        }

        if (rec.Status != SshApprovalRecord.Pending || rec.Command == null)
        {
            Console.Error.WriteLine($"คำขอนี้ {rec.Status} ไปแล้ว (already {rec.Status})");
            return 1;
        }

        var shown = SecretRedactor.Redact(rec.Command);
        var local = rec.RequestedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        Console.WriteLine();
        Console.WriteLine($"  คำขออนุมัติคำสั่ง SSH  ·  {rec.Id}");
        Console.WriteLine("  ─────────────────────────────────────────────");
        Console.WriteLine($"  เซิร์ฟเวอร์ : {rec.ProfileId} ({rec.User}@{rec.Host})");
        Console.WriteLine($"  ขอโดย    : {rec.RequestedBy} เมื่อ {local} · หมดอายุในอีก {Math.Max(0, (rec.ExpiresAt - DateTime.UtcNow).TotalMinutes):0} นาที");
        if (rec.ProfileRequiresConfirmation)
            Console.WriteLine("  profile นี้ตั้ง require_confirmation ไว้ ทุกคำสั่งต้องได้รับอนุมัติ");
        foreach (var r in rec.Reasons)
            Console.WriteLine($"  เหตุผล    : {(r.Catastrophic ? "⚠ " : "")}{r.ReasonTh} ({r.Rule})");
        Console.WriteLine("  คำสั่ง    :");
        foreach (var l in shown.Split('\n')) Console.WriteLine("      " + l.TrimEnd('\r'));
        if (!string.Equals(shown, rec.Command, StringComparison.Ordinal))
            Console.WriteLine("  (รหัสผ่าน/โทเค็นในคำสั่งถูกซ่อนตอนแสดงผล — คำสั่งจริงจะรันตามที่ agent ส่งมาทุกตัวอักษร)");
        Console.WriteLine();

        bool approved;
        if (rec.Catastrophic)
        {
            Console.Write($"  ⚠ คำสั่งนี้ทำลายข้อมูลแบบกู้คืนไม่ได้ — พิมพ์ชื่อ profile ({rec.ProfileId}) เพื่ออนุมัติ หรือ Enter เพื่อปฏิเสธ: ");
            approved = string.Equals(Console.ReadLine()?.Trim(), rec.ProfileId, StringComparison.Ordinal);
        }
        else
        {
            Console.Write("  พิมพ์ yes เพื่ออนุมัติ (อย่างอื่น = ปฏิเสธ): ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            approved = answer is "yes" or "y" or "ใช่";
        }

        var (ok, why) = approved ? store.Approve(id, "owner-cli") : store.Deny(id, "owner-cli");
        if (!ok) { Console.Error.WriteLine($"  ทำไม่ได้: {why}"); return 1; }
        AuditSsh(profile, approved ? "ssh_confirm_approved" : "ssh_confirm_denied", shown, null, id, null, "owner");
        Console.WriteLine(approved
            ? $"  ✅ อนุมัติแล้ว — ใช้ได้ครั้งเดียวภายใน {SshApprovalStore.ApprovedTtl.TotalMinutes:0} นาที บอก agent ให้เรียก ssh_run ซ้ำด้วย confirm_id {id}"
            : "  ❌ ไม่อนุมัติ — agent จะได้รับแจ้งว่าเจ้าของปฏิเสธ");
        return 0;
    }

    /// <summary>
    /// <c>brainx-mcp ssh-lint [--vault PATH]</c> — what each profile lets
    /// through, the deny/allow lists worth adopting, and how many past
    /// commands the approval gate would have stopped. Reads only; never
    /// prints a command or a credential.
    /// </summary>
    internal static int SshLintCli(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--vault" && Directory.Exists(args[i + 1])) _vaultPath = Path.GetFullPath(args[i + 1]);

        var profiles = SshStore.LoadAll();
        Console.WriteLine($"brainx-mcp ssh-lint · v{ServerVersion}");
        Console.WriteLine($"  profiles: {SshStore.ConfigPath} ({profiles.Count})");
        foreach (var p in profiles)
        {
            Console.WriteLine();
            Console.WriteLine($"▸ {p.Id}  (allow {p.AllowPatterns.Count} · deny {p.DenyPatterns.Count} · max {p.MaxRuntimeSec}s · confirm {(p.RequireConfirmation ? "every call" : "destructive only")})");
            var findings = SshProfileLint.Lint(p);
            if (findings.Count == 0) Console.WriteLine("  [ok] nothing to report");
            foreach (var f in findings) Console.WriteLine($"  [{f.Level}] {f.Message}");
            foreach (var sample in SshProfileLint.DangerousSamples)
            {
                var decision = CommandGuard.Validate(sample, p);
                var gated = SshCommandRisk.Classify(sample).Count > 0 || p.RequireConfirmation;
                var verdict = !decision.Allowed ? "refused by profile" : gated ? "needs owner approval" : "RUNS UNATTENDED";
                Console.WriteLine($"      {verdict,-22} {sample}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Recommended hard-deny patterns for a full-access profile (paste into deny_patterns — refused even with approval):");
        Console.WriteLine(JsonConvert.SerializeObject(SshProfileLint.RecommendedDenyPatterns, Formatting.Indented));
        Console.WriteLine();
        Console.WriteLine("Proposed allow_patterns for a separate READ-ONLY profile (same host/key, no approval needed, nothing can be chained):");
        Console.WriteLine(JsonConvert.SerializeObject(SshProfileLint.ReadOnlyAllowProposal, Formatting.Indented));

        // How much friction the gate adds, measured on what agents actually ran.
        var history = ReadSshHistoryCommands();
        if (history.Count > 0)
        {
            var show = args.Contains("--show", StringComparer.OrdinalIgnoreCase);
            var perRule = new Dictionary<string, int>(StringComparer.Ordinal);
            var examples = new List<string>();
            int gated = 0;
            foreach (var c in history)
            {
                var hits = SshCommandRisk.Classify(c);
                if (hits.Count == 0) continue;
                gated++;
                foreach (var r in hits.Select(h => h.Rule).Distinct())
                    perRule[r] = perRule.GetValueOrDefault(r) + 1;
                // Excerpts are redacted by the classifier; never the raw command.
                if (show) examples.Add($"  {string.Join(",", hits.Select(h => h.Rule)),-28} {hits[0].Excerpt}");
            }
            Console.WriteLine();
            Console.WriteLine($"History: {gated} of {history.Count} recorded commands would have needed the owner's approval.");
            foreach (var kv in perRule.OrderByDescending(k => k.Value))
                Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
            if (show) { Console.WriteLine(); examples.ForEach(Console.WriteLine); }
            else Console.WriteLine("  (--show lists each one, redacted)");
        }
        return 0;
    }

    /// <summary>
    /// Every SSH command on record, oldest first: legacy rows still in the
    /// access log, then the audit file and its rotations. Redacted rows are
    /// fine here — the classifier reads shape, not secrets.
    /// </summary>
    private static List<string> ReadSshHistoryCommands()
    {
        var cmds = new List<string>();
        var accessLog = Path.Combine(_vaultPath, ".obsidianx", "access-log.ndjson");
        foreach (var o in ReadNdjson(accessLog))
            if (IsLegacySshAuditRow(o)) cmds.Add(o["context"]?.ToString() ?? "");
        foreach (var file in NdjsonLog.Rotations(SshAuditPath).Reverse().Append(SshAuditPath))
            foreach (var o in ReadNdjson(file))
                if (o["op"]?.ToString() is "ssh_ok" or "ssh_fail") cmds.Add(o["command"]?.ToString() ?? "");
        return cmds.Where(c => c.Length > 0).ToList();
    }

    private static IEnumerable<JObject> ReadNdjson(string path)
    {
        if (!File.Exists(path)) yield break;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JObject? o = null;
            try { o = JObject.Parse(line); } catch { }
            if (o != null) yield return o;
        }
    }

    /// <summary>An SSH audit row written before 2026-09-23: full command in `context`, no pulse marker.</summary>
    private static bool IsLegacySshAuditRow(JObject o) =>
        (o["op"]?.ToString() ?? "").StartsWith("ssh_", StringComparison.OrdinalIgnoreCase)
        && o["pulse"]?.Type != JTokenType.Boolean;

    /// <summary>
    /// <c>brainx-mcp ssh-audit-migrate [--vault PATH] [--apply]</c>
    ///
    /// Moves the SSH audit rows written before the gate out of
    /// access-log.ndjson and into the redacted audit file. Dry run by default:
    /// it rewrites a file the owner keeps, and the rows it moves are the ones
    /// holding plaintext credentials, so the owner decides when. With --apply
    /// the original is copied OUTSIDE the vault first.
    /// </summary>
    internal static int SshAuditMigrateCli(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--vault" && Directory.Exists(args[i + 1])) _vaultPath = Path.GetFullPath(args[i + 1]);
        var apply = args.Contains("--apply", StringComparer.OrdinalIgnoreCase);
        var logPath = Path.Combine(_vaultPath, ".obsidianx", "access-log.ndjson");
        if (!File.Exists(logPath)) { Console.WriteLine($"no access log at {logPath} — nothing to migrate"); return 0; }

        Mutex? logLock = null;
        if (apply)
        {
            logLock = AcquireVaultLock(10_000, "accesslog");
            if (logLock == null) { Console.Error.WriteLine("another process is holding the access log — try again in a moment"); return 1; }
        }

        try
        {
            var lines = File.ReadAllLines(logPath, Encoding.UTF8);
            var keep = new List<string>(lines.Length);
            var moved = new List<JObject>();
            int withSecrets = 0;
            long movedBytes = 0;
            DateTime newest = DateTime.MinValue;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject? o = null;
                try { o = JObject.Parse(line); } catch { }
                if (o == null || !IsLegacySshAuditRow(o)) { keep.Add(line); continue; }

                var context = o["context"]?.ToString() ?? "";
                if (SecretRedactor.ContainsSecret(context)) withSecrets++;
                movedBytes += Encoding.UTF8.GetByteCount(line) + 1;
                var nodeId = o["node_id"]?.ToString() ?? "";
                var row = new JObject
                {
                    ["ts"] = o["ts"],
                    ["op"] = o["op"],
                    ["profile_id"] = nodeId.StartsWith("ssh:", StringComparison.Ordinal) ? nodeId[4..] : nodeId,
                    ["agent"] = o["agent"] ?? "",
                    ["command"] = SecretRedactor.Redact(context),
                    ["migrated"] = true
                };
                if (DateTime.TryParse(o["ts"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var ts) && ts > newest)
                    newest = ts;
                moved.Add(row);
            }

            var before = new FileInfo(logPath).Length;
            Console.WriteLine($"brainx-mcp ssh-audit-migrate · v{ServerVersion}");
            Console.WriteLine($"  access log      : {logPath} ({before / 1024.0 / 1024.0:0.00} MB, {lines.Length:n0} rows)");
            Console.WriteLine($"  legacy SSH rows : {moved.Count:n0} ({movedBytes / 1024.0 / 1024.0:0.00} MB) — {withSecrets} carry a credential in plain text");
            Console.WriteLine($"  after migration : ~{(before - movedBytes) / 1024.0 / 1024.0:0.00} MB");

            if (moved.Count == 0) { Console.WriteLine("  nothing to do"); return 0; }
            if (!apply)
            {
                Console.WriteLine();
                Console.WriteLine("  DRY RUN — nothing changed. Re-run with --apply to move them. The original access log is copied to");
                Console.WriteLine($"  {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "backups")} first (outside the vault).");
                return 0;
            }

            var backupDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "backups");
            Directory.CreateDirectory(backupDir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var backup = Path.Combine(backupDir, $"access-log-{stamp}.ndjson");
            File.Copy(logPath, backup, overwrite: false);

            // Shaped like a rotation dated by its newest row, so the audit
            // file's retention treats it as the oldest history it holds.
            // InvariantCulture: on this th-TH machine a culture-formatted yyyy is
            // the Buddhist era (2569), the bug that forked the session journals.
            var legacyStamp = (newest == DateTime.MinValue ? DateTime.UtcNow : newest)
                .ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var legacyName = $"ssh-audit.{legacyStamp}-000.ndjson";
            var legacyPath = Path.Combine(_vaultPath, ".obsidianx", legacyName);
            var auditLock = AcquireVaultLock(10_000, "sshaudit");
            try
            {
                NdjsonLog.AtomicWrite(legacyPath, string.Join("\n", moved.Select(r => r.ToString(Formatting.None))) + "\n");
            }
            finally { ReleaseVaultLock(auditLock); }

            NdjsonLog.AtomicWrite(logPath, string.Join("\n", keep) + "\n");
            NdjsonLog.ResetTrimFloor(logPath);

            Console.WriteLine();
            Console.WriteLine($"  moved {moved.Count:n0} rows → {legacyPath} (redacted)");
            Console.WriteLine($"  access log now {new FileInfo(logPath).Length / 1024.0 / 1024.0:0.00} MB");
            Console.WriteLine($"  backup of the original: {backup}");
            Console.WriteLine($"  ⚠ the backup still holds the {withSecrets} credential-bearing rows — delete it once you are satisfied.");
            return 0;
        }
        finally { ReleaseVaultLock(logLock); }
    }
}
