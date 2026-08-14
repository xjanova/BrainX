using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Wake — the hand-off finishes itself.
//
// The owner's complaint (2026-08-14): "ตอนนี้ ฉันต้องคอยกำกับบอกทั้งสองฝั่ง ...
// เมื่อฝั่งหนึ่งเสร็จจะทำงานต่อ ต้องส่งต่อปลุกให้อีกฝั่งหนึ่งรู้และทำงานต่อ ...
// ตอนนี้ฉันต้องมากระตุ้นเอง ไม่งั้นงานไม่เดิน".
//
// Correct, and the MCP layer cannot fix it. MCP has no server→model push: the
// taskQueue notice rides on an agent's NEXT tool response, and an agent that
// has finished answering never makes one. Every queued task sat there until a
// human typed something. The queue worked; nothing consumed it.
//
// Claude Code hooks are the missing edge, because a hook is a PROCESS the
// harness starts — it does not need the model to be doing anything:
//
//   Stop          fires when Claude finishes answering. Exit 2 puts stderr
//                 back in front of the model and it KEEPS WORKING. That is
//                 the wake: finish a turn with a task queued for you, and the
//                 next thing you see is the task.
//   SessionStart  fires when a session opens. stdout becomes context, so a
//                 session that opens with work waiting starts already knowing.
//
// WHY THIS IS A SUBCOMMAND AND NOT A POWERSHELL ONE-LINER. The existing
// auto-ingest hook is inline PowerShell, and it has now cost two bugs that a
// compiler would have refused: v1 read an environment variable Claude Code
// never sets (silently never fired for a whole release), and v3 wrote a BOM
// that ate the first line of every feed. This logic reads YAML frontmatter and
// makes a control-flow decision. It belongs in the binary that already has a
// task parser, where the compiler checks it, CI runs it, and macOS and Linux
// get it for free.
//
// LOOP SAFETY, in three layers, because a Stop hook that always says "keep
// going" is an infinite agent:
//   1. stop_hook_active — set by Claude Code when this turn is ALREADY a
//      continuation from a stop hook. Never continue twice in a row off one.
//   2. status: open — claiming a task takes it out of the trigger set, so the
//      normal path self-terminates after one wake.
//   3. A cooldown stamp per task id. An agent that reads a task and neither
//      claims nor finishes it must not be re-woken by that same task forever.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    /// <summary>
    /// How long a task stays quiet after it has woken somebody. Long enough
    /// that a real work session is never interrupted by its own task, short
    /// enough that a task dropped by a crashed session comes back the same
    /// morning.
    /// </summary>
    private static readonly TimeSpan WakeCooldown = TimeSpan.FromHours(2);

    private static string WakeStampDir => Path.Combine(BusRoot, "wake");

    /// <summary>
    /// <c>brainx-mcp hook-stop --vault PATH</c> — the Stop hook.
    ///
    /// Exit 0 = let Claude stop. Exit 2 = stderr goes back to the model and it
    /// continues. Any other exit code is a hook error, which Claude Code shows
    /// the user; that is why every failure path here returns 0. A brain that
    /// cannot read its own queue must fail closed and let the session end, not
    /// nag the owner with a stack trace.
    /// </summary>
    public static int RunStopHook(string[] args)
    {
        try
        {
            var payload = ReadHookPayload();

            // Layer 1. Claude Code sets this when the turn now ending was
            // itself started by a stop hook. Continuing again here is how a
            // wake becomes a loop.
            if (payload?["stop_hook_active"]?.ToObject<bool?>() == true) return 0;

            if (!TryEnterVault(args)) return 0;

            var me = WakeAudience(args);
            var tasks = OpenTasksForWake(me);
            var mail = UnreadMailForWake(me);
            if (tasks.Count == 0 && mail.Count == 0) return 0;

            // Layer 3. Only tasks/messages that have not woken anyone recently
            // count. Mail shares the stamp space because its id is unique too.
            var fresh = tasks.Where(t => !WakeIsCoolingDown(t.Id)).ToList();
            var freshMail = mail.Where(m => !WakeIsCoolingDown(m.Id)).ToList();
            if (fresh.Count == 0 && freshMail.Count == 0) return 0;
            foreach (var t in fresh) StampWake(t.Id);
            foreach (var m in freshMail) StampWake(m.Id);

            Console.Error.WriteLine(BuildWakeMessage(fresh, freshMail, forStop: true));
            return 2;
        }
        catch
        {
            // Fail closed: a broken wake must never be able to trap a session
            // in a turn it cannot end.
            return 0;
        }
    }

    /// <summary>
    /// <c>brainx-mcp hook-session-start --vault PATH</c> — the SessionStart hook.
    ///
    /// stdout becomes context in the new session. Always exit 0: a session that
    /// cannot start because the brain had nothing to say is a worse outcome
    /// than a session that starts uninformed.
    /// </summary>
    public static int RunSessionStartHook(string[] args)
    {
        try
        {
            ReadHookPayload();   // drain stdin so the harness never blocks on the pipe
            if (!TryEnterVault(args)) return 0;

            var me = WakeAudience(args);
            var tasks = OpenTasksForWake(me);
            var mail = UnreadMailForWake(me);
            if (tasks.Count == 0 && mail.Count == 0) return 0;

            // No cooldown and no stamp here. Opening a session is the owner
            // asking "what is there", and the answer must not depend on
            // whether some earlier session was told the same thing.
            Console.WriteLine(BuildWakeMessage(tasks, mail, forStop: false));
            return 0;
        }
        catch { return 0; }
    }

    // ───────────── the queue read ─────────────

    private sealed record WakeTask(string Id, string Title, string Assignee, string Priority);

    /// <summary>
    /// Who this hook run is speaking for, from <c>--agent</c>.
    ///
    /// Hooks carry no MCP handshake, so the audience cannot be discovered the
    /// way a tool call discovers it — it has to be told. Claude Code was the
    /// only client with hooks when this shipped, so the audience was hardcoded;
    /// Codex has them too, with the same exit-2-plus-stderr contract, and the
    /// same binary serves both the moment it knows which inbox to look in.
    /// Defaults to claude-code so an already-installed hook keeps working
    /// unchanged.
    /// </summary>
    private static string WakeAudience(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--agent", StringComparison.OrdinalIgnoreCase))
            {
                var v = args[i + 1].Trim();
                if (v.Length > 0) return SanitizeAgentSlug(v);
            }
        return "claude-code";
    }

    /// <summary>
    /// Open tasks addressed to the agent this hook is speaking for, plus
    /// anything left unaddressed.
    /// </summary>
    private static List<WakeTask> OpenTasksForWake(string me)
    {
        var found = new List<WakeTask>();
        var dir = HandoffDir;
        if (!Directory.Exists(dir)) return found;

        foreach (var file in new DirectoryInfo(dir).GetFiles("*.md")
                     .OrderByDescending(f => f.LastWriteTimeUtc))
        {
            var head = ReadHead(file.FullName, 2048);
            if (head == null) continue;

            var fm = ParseFrontmatter(head);
            if (!fm.TryGetValue("task_id", out var id) || string.IsNullOrEmpty(id)) continue;

            // Layer 2. A claimed task has already woken somebody and somebody
            // is on it; only `open` is unfinished AND unowned.
            if (!string.Equals(fm.GetValueOrDefault("status", "open"), "open", StringComparison.OrdinalIgnoreCase))
                continue;

            // Same forgiving rule task_queue uses, against the audience this
            // hook speaks for. A sender who only knew the vendor wrote
            // "claude"; the default writes "claude-code"; both are this hook.
            var assignee = fm.GetValueOrDefault("assignee", "any");
            if (!AssigneeMatches(assignee, me)) continue;

            found.Add(new WakeTask(
                id,
                TitleOf(head, file.Name),
                assignee,
                fm.GetValueOrDefault("priority", "normal")));

            if (found.Count >= 5) break;
        }
        return found;
    }

    /// <summary>
    /// What the agent reads on waking. Written as an instruction rather than a
    /// notification: the Stop hook's stderr is the model's next input, and
    /// "you have 1 task" without a next step just produces a sentence back.
    /// </summary>
    private static string BuildWakeMessage(List<WakeTask> tasks, List<WakeMail> mail, bool forStop)
    {
        var what = new List<string>();
        if (tasks.Count > 0) what.Add($"{tasks.Count} task(s) queued");
        if (mail.Count > 0) what.Add($"{mail.Count} unread message(s)");

        var lines = new List<string>
        {
            (forStop ? "BrainX: do not stop yet — " : "BrainX: ")
                + string.Join(" and ", what) + " for you on this brain."
        };

        if (mail.Count > 0)
        {
            lines.Add("");
            foreach (var m in mail)
                lines.Add($"  ✉ from {m.From}" + (m.Topic.Length > 0 ? $" — {m.Topic}" : ""));
            lines.Add("Call agent_inbox NOW to read them, act on what they say, and reply with");
            lines.Add("agent_send. A peer that gets no answer assumes the work is still moving.");
        }

        if (tasks.Count > 0)
        {
            lines.Add("");
            foreach (var t in tasks)
                lines.Add($"  • {t.Id} — {t.Title}" + (t.Priority == "high" ? "  [HIGH]" : ""));
            lines.Add("");
            lines.Add("Another agent specced this work and cannot build it. Continue with it now:");
            lines.Add("  1. task_queue, then read the full spec (brain_get_note on its path) — the Context");
            lines.Add("     section is the half you cannot reconstruct from the repo.");
            lines.Add("  2. task_update {task_id, status:'claimed'} BEFORE you touch code.");
            lines.Add("  3. Build it, then task_update {task_id, status:'done', note:'…files changed…'}.");
            lines.Add("     That note is the only report the other side will ever see.");
            lines.Add("Tell your user which task you picked up and from whom.");
            lines.Add("If it is genuinely not yours to do, task_update {status:'blocked', note:'why'} —");
            lines.Add("do not leave it open, or it will wake the next session too.");
        }
        else
        {
            lines.Add("Tell your user what arrived and from whom — never work a peer's message silently.");
        }
        return string.Join('\n', lines);
    }

    // ───────────── unread mail ─────────────

    /// <summary>
    /// Peer mail sitting unread in this brain's inbox.
    ///
    /// A queued task was only half the problem. The Iron Ward MV stalled for an
    /// hour on 2026-08-14 with no task anywhere: Claude finished a turn saying
    /// "shot_001 is done, come pick a frame", Codex answered three times, and
    /// nobody ever saw it — a session that has answered makes no further tool
    /// call, and the agentBus notice can only ride on one. The work was done and
    /// on disk; the only thing missing was somebody being told.
    ///
    /// So mail wakes too. This deliberately does NOT consume anything — reading
    /// is agent_inbox's job and consuming here would destroy the message on the
    /// way to announcing it. The hook says "you have mail"; the model reads it.
    ///
    /// Every Claude session shares one "claude" inbox, so BOTH names are
    /// checked: whichever one the sender guessed is the one that holds it.
    /// </summary>
    private static List<WakeMail> UnreadMailForWake(string me)
    {
        // The agent's own box, plus its vendor box when those differ: the bus
        // collapses every Claude session into "claude", so a claude-code hook
        // that only looked at "claude-code" would never see the mail that
        // actually arrives. For codex the two are the same name and the set
        // collapses to one.
        var boxes = new List<string> { me };
        var dash = me.IndexOf('-');
        if (dash > 0) boxes.Add(me[..dash]);

        var found = new List<WakeMail>();
        foreach (var box in boxes.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(BusRoot, "inbox", box);
            if (!Directory.Exists(dir)) continue;

            foreach (var file in new DirectoryInfo(dir).GetFiles("*.json"))
            {
                try
                {
                    var o = JObject.Parse(File.ReadAllText(file.FullName));
                    var id = o["id"]?.ToString();
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    found.Add(new WakeMail(
                        id!,
                        o["from"]?.ToString() ?? "another agent",
                        o["topic"]?.ToString() ?? ""));
                }
                catch { /* a half-written message is next tick's problem */ }

                if (found.Count >= 5) return found;
            }
        }
        return found;
    }

    private sealed record WakeMail(string Id, string From, string Topic);

    // ───────────── cooldown ─────────────

    private static string WakeStampPath(string taskId) =>
        Path.Combine(WakeStampDir, taskId + ".stamp");

    private static bool WakeIsCoolingDown(string taskId)
    {
        try
        {
            var path = WakeStampPath(taskId);
            if (!File.Exists(path)) return false;
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < WakeCooldown;
        }
        // Unreadable stamp → treat as cooling down. Waking too little is a
        // task that waits; waking too much is an agent that will not stop.
        catch { return true; }
    }

    private static void StampWake(string taskId)
    {
        try
        {
            Directory.CreateDirectory(WakeStampDir);
            File.WriteAllText(WakeStampPath(taskId), DateTime.UtcNow.ToString("o"));
        }
        catch { /* a missed stamp costs one extra wake, not correctness */ }
    }

    // ───────────── plumbing ─────────────

    /// <summary>
    /// Read the hook's JSON payload from stdin. Claude Code always sends one;
    /// draining it matters even when we ignore the contents, because a hook
    /// that exits with the pipe unread can leave the harness writing into a
    /// closed handle.
    /// </summary>
    private static JObject? ReadHookPayload()
    {
        try
        {
            if (Console.IsInputRedirected)
            {
                var text = Console.In.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(text)) return JObject.Parse(text);
            }
        }
        catch { /* a hook with no payload is still a hook */ }
        return null;
    }

    /// <summary>
    /// Point the shared vault-dependent helpers (HandoffDir, BusRoot) at the
    /// right vault. Returns false when there is no vault to read, which is the
    /// signal to exit quietly — a machine with no BrainX vault should not have
    /// its Claude Code sessions commented on.
    /// </summary>
    private static bool TryEnterVault(string[] args)
    {
        // Only --vault is read; anything else is ignored on purpose. The
        // installed command carries a --tag marker so its version is greppable
        // in settings.json, and a hook that refused to run because it did not
        // recognise its own version stamp would be a spectacular own goal.
        string? explicitVault = null;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--vault", StringComparison.OrdinalIgnoreCase))
                explicitVault = args[i + 1];

        var vault = !string.IsNullOrWhiteSpace(explicitVault) && Directory.Exists(explicitVault)
            ? Path.GetFullPath(explicitVault)
            : Environment.GetEnvironmentVariable("BRAINX_VAULT");

        if (string.IsNullOrWhiteSpace(vault) || !Directory.Exists(vault)) return false;

        _vaultPath = Path.GetFullPath(vault);
        return true;
    }
}
