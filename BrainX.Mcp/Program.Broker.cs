using System.Diagnostics;
using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Broker — the loop runs itself.
//
// The owner's complaint (2026-09-19): "ตอนนี้ที่ claude คุยกับ codex ฉันต้องไป
// คอยพิมพ์บอกให้ อีกตัวคอยไปอ่านคำขอ ... ฉันต้องการให้มันทำงานประสานงานกันเอง
// จนกว่างานลุล่วงแล้วรายงานเรา" — and then the diagnosis that mattered:
// "codex เขาไม่มีรันทาร์ก ทิ้งไว้เพื่อรอการทำงานเหมือน claude ไง เราต้องหา
// วิธีอื่น เพื่อไม่ให้เกิดปัญหา กับ ตัวอื่นๆ ด้วย".
//
// That is the whole bug, stated exactly. Program.Wake.cs bought the loop a
// real edge — a hook is a PROCESS, it does not need the model to be doing
// anything — but a Stop hook only fires at the END OF A TURN OF A LIVE
// SESSION. So the wake has a hidden premise: that the recipient is sitting
// there, parked, with a session open. Claude Code parks. Codex does not. Nor
// will Gemini, Grok, or anything else driven from a CLI. For every agent but
// one, there was never a session to wake, and the mail sat in inbox/<agent>/
// until a human opened that vendor's app and typed.
//
// So stop waking sessions and start CREATING them. The broker owns the loop:
// it watches the same queue the hooks watch, and when an agent has work and no
// live session, it SPAWNS one headless from a per-agent runner in
// runners.json. A new vendor costs one config entry, not an integration —
// which is the "ไม่ให้เกิดปัญหากับตัวอื่นๆ ด้วย" half of the ask.
//
// WHY A SUBCOMMAND OF THIS BINARY (owner's call, 2026-09-19). The task parser,
// the bus, the identity rules and the frontmatter reader are all already here
// and already tested; the client is a GUI the owner closes; and deploy-mcp.ps1
// already knows how to hot-swap this exe while it runs. Same reasoning that
// put the wake hooks here instead of in PowerShell.
//
// THE PROMPT HANDED TO A SPAWNED AGENT CARRIES NO MESSAGE CONTENT — on
// purpose, and it is load-bearing in three separate ways:
//   1. Injection. Mail bodies are written by other agents. Interpolating one
//      into a command line hands a peer a shell.
//   2. Quoting. PowerShell 5.1 mangles embedded double quotes in native args
//      (see the Codex headless note) — a body with a quote in it breaks the
//      spawn, silently and only sometimes.
//   3. Delivery semantics. Mail is consume-on-read. Pasting a copy into a
//      prompt means the message is delivered twice and read zero times.
// The agent is told it HAS work and to call agent_inbox/task_queue itself.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    private static string BrokerDir => Path.Combine(BusRoot, "broker");
    private static string BrokerConfigPath => Path.Combine(BusRoot, "runners.json");
    private static string BrokerDecisionDir => Path.Combine(BrokerDir, "decisions");

    /// <summary>
    /// What a spawned agent is told. Carries no message CONTENT (see the
    /// header note) but it must carry the work LABELS, and that distinction
    /// cost three days of stalled mail.
    ///
    /// <c>agent_inbox</c> is work-scoped: labelled mail is invisible to a
    /// plain call and comes back only as an `otherWork` count. So a prompt
    /// that said "call agent_inbox" sent the agent looking in the one place
    /// the mail was guaranteed not to be — it found nothing, exited, and the
    /// next tick spawned it again. An infinite spawn loop that makes no
    /// progress, and from the outside indistinguishable from an empty queue.
    ///
    /// Found on the owner's real vault, never in the tests: every message in
    /// the temp vaults was unlabelled, so this path was not once exercised.
    /// The oldest stuck message was three days old.
    /// </summary>
    private static string BuildSpawnPrompt(WaitingWork work)
    {
        var sb = new StringBuilder(
            "BrainX broker: work is queued for you on this brain and nobody is driving this session. ");

        if (work.Works.Count > 0)
        {
            // Named explicitly, one call per label. An agent told only that
            // "labels exist" still has to guess them, and a wrong guess is
            // indistinguishable from an empty inbox.
            sb.Append("The mail waiting for you is LABELLED, and a plain agent_inbox will not return it. ")
              .Append("Read every one of these: ")
              .Append(string.Join(", ", work.Works.Select(InboxCallFor)))
              .Append(". ");
        }
        else
        {
            sb.Append("Call agent_inbox now. ");
        }

        sb.Append("Also call task_queue. Read what is waiting and do it. ")
          .Append("Close the loop with task_update, and reply to whoever wrote to you with agent_send ")
          .Append("KEEPING THE SAME work label, or your answer is invisible to them for the same reason. ")
          .Append("Do not stop while work you can do is still open. ")
          .Append("If a choice needs the OWNER (not a peer) - scope, money, anything destructive, or two ")
          .Append("defensible options - call agent_ask_user instead of guessing, then stop.");
        return sb.ToString();
    }

    /// <summary>One work-scoped inbox call, written the way an agent types it.</summary>
    private static string InboxCallFor(string work) =>
        "agent_inbox {work:'" + work + "'}";

    /// <summary>
    /// <c>brainx-mcp broker --vault PATH [--once] [--dry-run]</c>.
    ///
    /// Runs until killed. <c>--once</c> does a single tick and exits, which is
    /// what the tests and a Scheduled Task trigger use; <c>--dry-run</c> makes
    /// every spawn decision and prints it without starting anything, which is
    /// the only safe way to look at what this would do on a live vault.
    /// </summary>
    public static async Task<int> RunBroker(string[] args)
    {
        if (!TryEnterVault(args))
        {
            Console.Error.WriteLine("broker: no vault. Pass --vault PATH or set BRAINX_VAULT.");
            return 1;
        }

        _brokerIsCli = true;
        var once = args.Any(a => a.Equals("--once", StringComparison.OrdinalIgnoreCase));
        var dryRun = args.Any(a => a.Equals("--dry-run", StringComparison.OrdinalIgnoreCase));

        Directory.CreateDirectory(BrokerDir);
        var cfg = LoadBrokerConfig();

        BrokerLog($"broker up · vault={_vaultPath} · runners={string.Join(", ", cfg.Runners.Keys)}"
                  + (dryRun ? " · DRY RUN" : ""));

        // Children this broker started, so a Ctrl-C or a service stop does not
        // leave headless agents running against the owner's repos with nobody
        // watching them. A spawned agent is a process with write access; it
        // must not outlive the thing that is supposed to be supervising it.
        var live = new Dictionary<string, BrokerRun>(StringComparer.OrdinalIgnoreCase);
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll(live);

        try
        {
            do
            {
                try { await BrokerTick(cfg, live, dryRun).ConfigureAwait(false); }
                catch (Exception ex) { BrokerLog("tick failed: " + Redact(ex.Message)); }

                if (once) { await SuperviseToCompletion(cfg, live).ConfigureAwait(false); break; }
                try { await Task.Delay(TimeSpan.FromSeconds(cfg.PollSeconds), stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            } while (!stopping.IsCancellationRequested);
        }
        // Whatever this broker started, it takes with it. A spawned agent has
        // write access to the owner's repos and must never outlive the thing
        // supervising it.
        finally { KillAll(live); }

        BrokerLog("broker down");
        return 0;
    }

    /// <summary>
    /// <c>--once</c> waits for what it started, instead of returning the
    /// instant the spawn call does.
    ///
    /// The obvious design was the other one — a trigger fires, the agent runs
    /// on, the next trigger reaps it — and it does not survive contact with
    /// Windows. Measured: the spawned cmd.exe was already DEAD by the time the
    /// next command in the same shell could look at its pid, because a parent
    /// that exits takes its tree with it whenever anything up the chain holds
    /// the processes in a job object — a Claude Code tool call does, and so
    /// does Task Scheduler on the settings most people pick.
    ///
    /// So supervision is not the thing to give up to make a trigger work.
    /// A tick that stays until its agents are done keeps every invariant
    /// (one run per agent, MaxRunSeconds enforced, nothing orphaned) and costs
    /// only wall-clock in a process whose entire job is to wait.
    /// </summary>
    private static async Task SuperviseToCompletion(BrokerConfig cfg, Dictionary<string, BrokerRun> live)
    {
        while (live.Count > 0)
        {
            await Task.Delay(500).ConfigureAwait(false);
            ReapFinishedRuns(cfg, live);
        }
    }

    // ───────────── one tick ─────────────

    private static async Task BrokerTick(BrokerConfig cfg, Dictionary<string, BrokerRun> live, bool dryRun)
    {
        ReapFinishedRuns(cfg, live);

        foreach (var agent in AgentsWithWaitingWork())
        {
            // A run is still going: that IS the session. Spawning a second
            // one would have two agents consuming the same consume-on-read
            // inbox, which is the exact race the bus was built to avoid — and
            // the reason this check is by agent, not by task. Disk as well as
            // memory, so it holds across `--once` ticks and restarts.
            if (live.ContainsKey(agent)) continue;
            if (AdoptOrClearOrphanRun(cfg, agent)) continue;

            var work = WaitingWorkFor(agent);
            if (work.Mail == 0 && work.Tasks == 0) continue;

            // Work that is parked on the OWNER does not wake anybody. Asking
            // twice is worse than not asking: the second spawn cannot see the
            // first question, so it asks its own version of it and the owner
            // gets two notifications for one decision.
            if (HasOpenDecision(agent))
            {
                BrokerLog($"{agent}: {Describe(work)} — waiting on the owner, not spawning");
                continue;
            }

            var state = ReadRunnerState(agent);
            var verdict = ClassifySession(agent, state, cfg);
            SaveRunnerState(agent, state);

            switch (verdict)
            {
                case SessionVerdict.Working:
                    // Tool calls are still landing, so the piggyback will put
                    // anything new in front of this session on its own. That
                    // reasoning holds for mail that ARRIVED while it was
                    // working; it does not hold for mail that was already old
                    // when this session started.
                    //
                    // The owner caught this on the real vault: codex was
                    // online and active (30 calls, mid brain_append_note) with
                    // four messages pending, the oldest THREE DAYS old. The
                    // broker classified it Working and skipped it — silently,
                    // which is why nobody could see it was happening. "Busy"
                    // never meant "busy with this".
                    //
                    // A nudge to a working session is the cheapest delivery
                    // there is: it rides the piggyback onto its very next tool
                    // call. It is safe to repeat because HasPendingNudge keeps
                    // exactly one outstanding.
                    if (work.OldestHours * 60 >= cfg.StaleMailMinutes)
                    {
                        if (dryRun) { BrokerLog($"{agent}: would nudge busy session about stale work ({Describe(work)})"); continue; }
                        NudgeParkedSession(agent, work);
                        continue;
                    }
                    // Fresh work on an active session: genuinely nothing to do,
                    // but SAY so. A skip with no log line is indistinguishable
                    // from an empty queue, which is the report that hid the
                    // three-day-old mail in the first place.
                    BrokerLog($"{agent}: {Describe(work)} — session is active, the piggyback has it");
                    continue;

                case SessionVerdict.Parked:
                    // A live session that has gone quiet. A bus message is the
                    // cheapest thing to try: the piggyback puts it in front of
                    // the session the instant anything touches a tool.
                    //
                    // But it is only a TRY, and the difference matters. A
                    // nudge reaches a parked agent through the piggyback (which
                    // needs a tool call), its Stop hook (which needs a turn to
                    // end) or SessionStart (which needs a new session) — and a
                    // session parked at a prompt does none of the three. The
                    // harness owns stdin; nothing here can make it read.
                    //
                    // Measured on the live vault: codex heartbeating every few
                    // seconds with `calls` frozen at 30 and five messages
                    // behind it, the oldest three days old. The nudge landed
                    // in a box nothing was going to open.
                    //
                    // So a nudge that has not been answered becomes a spawn. A
                    // headless run is a NEW session with its own identity, and
                    // the work label is what keeps it off the parked session's
                    // other mail.
                    if (work.OldestHours * 60 < cfg.ParkedSpawnAfterMinutes)
                    {
                        if (dryRun) { BrokerLog($"{agent}: would nudge parked session ({Describe(work)})"); continue; }
                        NudgeParkedSession(agent, work);
                        continue;
                    }

                    BrokerLog($"{agent}: parked and unresponsive with {Describe(work)} — a nudge cannot reach it, spawning");
                    goto case SessionVerdict.Absent;

                case SessionVerdict.Absent:
                    if (!cfg.Runners.TryGetValue(agent, out var runner))
                    {
                        // The honest failure. Silence here is how the owner
                        // ends up believing the loop is running when the mail
                        // is simply piling up for an agent nobody can start.
                        BrokerLog($"{agent}: {Describe(work)} waiting, no session and no runner in runners.json — nobody can pick this up");
                        await EscalateAsync(cfg, new BrokerDecision(
                            Id: "norunner-" + agent,
                            Agent: agent,
                            Work: "",
                            Question: $"'{agent}' has {Describe(work)} waiting but no live session and no entry in runners.json, so nothing can pick it up.",
                            Options: new[] { "add a runner for it", "reassign the work", "clear it" })).ConfigureAwait(false);
                        continue;
                    }

                    var gate = BudgetGate(cfg, agent, state);
                    if (gate != null)
                    {
                        BrokerLog($"{agent}: budget stop — {gate}");
                        await EscalateAsync(cfg, new BrokerDecision(
                            Id: "budget-" + agent,
                            Agent: agent,
                            Work: "",
                            Question: $"'{agent}' hit a broker budget: {gate}. The work is still open.",
                            Options: new[] { "let it keep going", "stop and leave it for me" })).ConfigureAwait(false);
                        continue;
                    }

                    // Where the work LIVES is not guessable, and guessing it
                    // is the expensive kind of wrong: a headless agent pointed
                    // at the wrong repo does real work in the wrong place. The
                    // runner's cwd is a default for unlabelled work only.
                    var unmapped = work.Works.Where(w => !cfg.WorkDirs.ContainsKey(w)).ToList();
                    if (unmapped.Count > 0)
                    {
                        await EscalateAsync(cfg, new BrokerDecision(
                            Id: "workdir-" + unmapped[0],
                            Agent: agent,
                            Work: unmapped[0],
                            Question: $"'{agent}' has work labelled '{unmapped[0]}' waiting ({Describe(work)}) and no session to do it in. "
                                    + $"Which folder should it run in? Right now it would default to {runner.Cwd}, which is probably wrong. "
                                    + "Add it under \"workDirs\" in runners.json.",
                            Options: new[] { $"use {runner.Cwd} anyway", "I will add it to runners.json" })).ConfigureAwait(false);
                        continue;
                    }

                    if (dryRun) { BrokerLog($"{agent}: would spawn {runner.Exe} in {WorkDirFor(cfg, runner, work)} ({Describe(work)})"); continue; }
                    SpawnRunner(cfg, agent, runner, work, live, state);
                    continue;
            }
        }

        await PumpDecisionsAsync(cfg).ConfigureAwait(false);
    }

    // ───────────── is anybody home? ─────────────

    private enum SessionVerdict { Working, Parked, Absent }

    /// <summary>
    /// The three-way question the whole design turns on, answered from the
    /// presence file the bus already writes.
    ///
    /// Presence alone cannot answer it. A parked Claude Code session and a
    /// Codex run in the middle of a build both report "online" — the heartbeat
    /// says the MCP process is alive, not that the MODEL is doing anything.
    /// What separates them is the <c>calls</c> counter, which
    /// <see cref="NoteBusActivity"/> advances on every served tool call: a
    /// session that is working moves it, a session waiting on a human does
    /// not. So "online but the counter has not moved for idleGraceSeconds" is
    /// the signature of a session parked at a prompt, and that is a nudge, not
    /// a spawn.
    ///
    /// The grace period is what keeps a thinking model from being declared
    /// parked mid-thought.
    /// </summary>
    private static SessionVerdict ClassifySession(string agent, RunnerState state, BrokerConfig cfg)
    {
        var age = PresenceAgeSeconds(agent);
        if (!IsOnline(age)) { state.LastCalls = null; state.CallsStillSince = null; return SessionVerdict.Absent; }

        var calls = PresenceCalls(agent);
        var now = DateTime.UtcNow;

        if (calls != state.LastCalls)
        {
            state.LastCalls = calls;
            state.CallsStillSince = now;
            return SessionVerdict.Working;
        }

        state.CallsStillSince ??= now;
        return (now - state.CallsStillSince.Value).TotalSeconds >= cfg.IdleGraceSeconds
            ? SessionVerdict.Parked
            : SessionVerdict.Working;
    }

    private static long? PresenceCalls(string agent)
    {
        try
        {
            var path = Path.Combine(BusPresenceDir, agent + ".json");
            if (!File.Exists(path)) return null;
            return JObject.Parse(File.ReadAllText(path))["calls"]?.ToObject<long?>();
        }
        catch { return null; }
    }

    /// <summary>
    /// Put the work in front of a live-but-idle session. Deliberately a bus
    /// message and not something cleverer: it rides the piggyback that already
    /// exists, it is the one channel every agent on this brain can already
    /// read, and it leaves an audit trail in the same place as every other
    /// exchange. Rate-limited by the same wake stamp the hooks use, so a
    /// session that is idle for an hour is not told forty times.
    /// </summary>
    private static void NudgeParkedSession(string agent, WaitingWork work)
    {
        // Never stack nudges. The wake stamp alone was not enough: its window
        // is two hours, which is right for "this task already woke somebody"
        // and badly wrong for a live session with work sitting in front of it.
        // Dropping the window without this check would then pile a nudge into
        // the inbox every tick, burying the actual message under notices about
        // it. One pending nudge at a time is the invariant; the clock is only
        // the backstop.
        if (HasPendingNudge(agent)) return;

        var stampId = "nudge-" + agent;
        if (NudgeIsCoolingDown(stampId)) return;
        StampWake(stampId);

        try
        {
            var scoped = work.Works.Count > 0
                ? "That mail is LABELLED — a plain agent_inbox will not return it. Read "
                  + string.Join(", ", work.Works.Select(InboxCallFor)) + ". "
                : "Call agent_inbox. ";
            var body = $"BrainX broker: you have {Describe(work)} waiting and this session has been idle. "
                     + scoped
                     + "Also call task_queue, do the work, and close it with task_update, replying with the "
                     + "same work label. If the OWNER has to decide something, call agent_ask_user.";
            DeliverBusMessage("broker", agent, body, topic: "broker-nudge", work: null);
            BrokerLog($"{agent}: nudged parked session ({Describe(work)})");
        }
        catch (Exception ex) { BrokerLog($"{agent}: nudge failed — {Redact(ex.Message)}"); }
    }

    /// <summary>
    /// Minutes, not the wake hook's hours. A nudge is not an announcement that
    /// a task exists — it is a live session being told there is something in
    /// front of it, and that is worth repeating on the timescale a person
    /// switches windows.
    /// </summary>
    private static readonly TimeSpan NudgeCooldown = TimeSpan.FromMinutes(3);

    private static bool NudgeIsCoolingDown(string id)
    {
        try
        {
            var path = WakeStampPath(id);
            if (!File.Exists(path)) return false;
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < NudgeCooldown;
        }
        catch { return true; }
    }

    /// <summary>Is a broker nudge already sitting unread in this agent's box?</summary>
    private static bool HasPendingNudge(string agent)
    {
        foreach (var box in BoxesFor(agent))
        {
            var dir = Path.Combine(BusRoot, "inbox", box);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var o = JObject.Parse(File.ReadAllText(f));
                    if (string.Equals(o["topic"]?.ToString(), "broker-nudge", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
        }
        return false;
    }

    // ───────────── spawning ─────────────

    private sealed class BrokerRun
    {
        public required Process Process { get; init; }
        public required string Agent { get; init; }
        public required DateTime StartedUtc { get; init; }
    }

    private static void SpawnRunner(BrokerConfig cfg, string agent, RunnerSpec runner,
                                    WaitingWork work, Dictionary<string, BrokerRun> live, RunnerState state)
    {
        var exe = ResolveRunnerExe(runner);
        if (exe == null)
        {
            BrokerLog($"{agent}: runner '{runner.Exe}' not found on PATH or in its fallbacks — cannot spawn");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = WorkDirFor(cfg, runner, work),
        };

        // ArgumentList, never a joined string. The runner's own arguments come
        // from a config file the owner edits, and the one substitution we make
        // ({prompt}) is a constant we control — but a joined command line would
        // still put quoting rules between this code and what actually runs,
        // and that is precisely the class of bug the Codex headless note cost
        // a session to find.
        foreach (var a in runner.Args)
            psi.ArgumentList.Add(a.Replace("{prompt}", BuildSpawnPrompt(work))
                                  .Replace("{cwd}", psi.WorkingDirectory)
                                  .Replace("{agent}", agent));

        // The child must not inherit this broker's vault override by accident
        // on one path and not another — set it explicitly so a spawned agent
        // always reads the same brain the broker is watching.
        psi.Environment["BRAINX_VAULT"] = _vaultPath;

        try
        {
            var p = Process.Start(psi);
            if (p == null) { BrokerLog($"{agent}: spawn returned no process"); return; }

            // Drain both pipes. A child whose stdout fills its buffer blocks
            // forever, which would look exactly like a hung agent — and the
            // output is the only forensic record of a headless run.
            // InvariantCulture, not the ambient one. This machine runs th-TH, where
            // "yyyy" is the Buddhist year: the first run of this code produced
            // `codex-25690919-050606.log`. A filename nobody can sort by date is
            // the mildest version of that bug; the same format elsewhere would
            // put 2569 into data other tools parse.
            var log = Path.Combine(BrokerDir, $"{agent}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.log");
            p.OutputDataReceived += (_, e) => AppendRunLog(log, e.Data);
            p.ErrorDataReceived += (_, e) => AppendRunLog(log, e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            var startedUtc = DateTime.UtcNow;
            live[agent] = new BrokerRun { Process = p, Agent = agent, StartedUtc = startedUtc };
            state.Spawns.Add(startedUtc);
            state.Hops++;
            state.RunPid = p.Id;
            state.RunStartedUtc = startedUtc;
            SaveRunnerState(agent, state);

            BrokerLog($"{agent}: spawned {Path.GetFileName(exe)} pid {p.Id} for {Describe(work)} · log {Path.GetFileName(log)}");
        }
        catch (Exception ex) { BrokerLog($"{agent}: spawn failed — {Redact(ex.Message)}"); }
    }

    /// <summary>
    /// The folder a run happens in: the one mapped to this work label, else
    /// the runner's default, else the vault. Labelled work whose folder is
    /// unknown never reaches here — BrokerTick asks the owner instead.
    /// </summary>
    private static string WorkDirFor(BrokerConfig cfg, RunnerSpec runner, WaitingWork work)
    {
        foreach (var w in work.Works)
            if (cfg.WorkDirs.TryGetValue(w, out var dir) && Directory.Exists(dir)) return dir;
        return Directory.Exists(runner.Cwd) ? runner.Cwd : _vaultPath;
    }

    /// <summary>
    /// Find the runner's executable. <c>codex</c> is NOT on PATH on the
    /// owner's machine — the desktop app hides it under a hashed directory —
    /// and CliInstall already carries that scar, so runners.json takes a list
    /// of fallbacks and a <c>*</c> in one is expanded against the filesystem.
    /// </summary>
    private static string? ResolveRunnerExe(RunnerSpec runner)
    {
        if (Path.IsPathRooted(runner.Exe) && File.Exists(runner.Exe)) return runner.Exe;
        if (OnPath(runner.Exe) is { } found) return found;

        foreach (var raw in runner.ExeFallbacks)
        {
            var pattern = Environment.ExpandEnvironmentVariables(raw);
            if (File.Exists(pattern)) return pattern;
            if (!pattern.Contains('*')) continue;

            // One wildcard segment, expanded shallowly. Deliberately not a
            // recursive search: the fallbacks name a known layout, and a
            // recursive walk of %LOCALAPPDATA% on every miss is a tick that
            // takes minutes.
            try
            {
                var star = pattern.IndexOf('*');
                var root = Path.GetDirectoryName(pattern[..star]);
                if (root == null || !Directory.Exists(root)) continue;
                var tail = pattern[(pattern.IndexOf(Path.DirectorySeparatorChar, star) + 1)..];
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var candidate = Path.Combine(dir, tail);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? OnPath(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : new[] { "" };
        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                try
                {
                    var c = Path.Combine(dir.Trim(), exe + ext.ToLowerInvariant());
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
        }
        return null;
    }

    private static void AppendRunLog(string path, string? line)
    {
        if (line == null) return;
        try { File.AppendAllText(path, Redact(line) + Environment.NewLine, new UTF8Encoding(false)); }
        catch { }
    }

    /// <summary>
    /// Retire finished runs, and kill the ones that have outstayed the budget.
    /// A headless agent that hangs holds its agent's slot forever, so every
    /// later tick sees "a run is already live" and the queue silently stops
    /// moving — the failure looks identical to having no work at all.
    /// </summary>
    private static void ReapFinishedRuns(BrokerConfig cfg, Dictionary<string, BrokerRun> live)
    {
        foreach (var (agent, run) in live.ToList())
        {
            if (!run.Process.HasExited)
            {
                if ((DateTime.UtcNow - run.StartedUtc).TotalSeconds < cfg.MaxRunSeconds) continue;
                BrokerLog($"{agent}: run exceeded {cfg.MaxRunSeconds}s — killing pid {run.Process.Id}");
                TryKill(run.Process);
            }

            var code = run.Process.HasExited ? run.Process.ExitCode : -1;
            BrokerLog($"{agent}: run finished, exit {code}, {(DateTime.UtcNow - run.StartedUtc).TotalSeconds:F0}s");
            run.Process.Dispose();
            live.Remove(agent);
            ClearRunRecord(agent);
        }
    }

    /// <summary>
    /// Is a run from an EARLIER tick or an earlier broker still going?
    /// Returns true when this agent must be left alone.
    ///
    /// Identity is pid plus start time. A bare pid match is wrong on Windows,
    /// where pids are reused aggressively: eventually some unrelated process
    /// inherits the number and the agent reads as permanently busy, which is
    /// indistinguishable from a queue that has quietly stopped moving.
    /// </summary>
    private static bool AdoptOrClearOrphanRun(BrokerConfig cfg, string agent)
    {
        var st = ReadRunnerState(agent);
        if (st.RunPid is not int pid || st.RunStartedUtc is not DateTime started) return false;

        try
        {
            using var p = Process.GetProcessById(pid);
            // Two seconds of slack: the recorded time is taken just after
            // Process.Start returns, the OS one just before the image runs.
            if (!p.HasExited && Math.Abs((p.StartTime.ToUniversalTime() - started).TotalSeconds) < 2)
            {
                if ((DateTime.UtcNow - started).TotalSeconds >= cfg.MaxRunSeconds)
                {
                    BrokerLog($"{agent}: orphan run pid {pid} exceeded {cfg.MaxRunSeconds}s — killing");
                    TryKill(p);
                    ClearRunRecord(agent);
                    return false;
                }
                BrokerLog($"{agent}: run pid {pid} still going ({(DateTime.UtcNow - started).TotalSeconds:F0}s) — leaving it");
                return true;
            }
        }
        catch { /* gone, or not ours to look at: treat as finished */ }

        ClearRunRecord(agent);
        return false;
    }

    private static void ClearRunRecord(string agent)
    {
        var st = ReadRunnerState(agent);
        st.RunPid = null;
        st.RunStartedUtc = null;
        SaveRunnerState(agent, st);
    }

    private static void KillAll(Dictionary<string, BrokerRun> live)
    {
        foreach (var (_, run) in live.ToList()) TryKill(run.Process);
        live.Clear();
    }

    private static void TryKill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    // ───────────── budget ─────────────

    /// <summary>
    /// Why the loop is allowed to stop. Returns null when a spawn is fine, or
    /// the reason it is not.
    ///
    /// Three ceilings, because an autonomous loop with no ceiling is a bill:
    /// hops per work item catches two agents politely handing the same job back
    /// and forth; spawns per hour catches a task nobody can finish being
    /// retried forever; and MaxRunSeconds (enforced in the reaper) catches one
    /// run that hangs.
    /// </summary>
    private static string? BudgetGate(BrokerConfig cfg, string agent, RunnerState state)
    {
        var hourAgo = DateTime.UtcNow.AddHours(-1);
        state.Spawns.RemoveAll(s => s < hourAgo);

        if (state.Spawns.Count >= cfg.MaxSpawnsPerHour)
            return $"{state.Spawns.Count} spawns in the last hour (max {cfg.MaxSpawnsPerHour})";
        if (state.Hops >= cfg.MaxHopsPerWork)
            return $"{state.Hops} hops without the work closing (max {cfg.MaxHopsPerWork})";
        return null;
    }

    // ───────────── what is waiting ─────────────

    private readonly record struct WaitingWork(
        int Mail, int Tasks, IReadOnlyList<string> Works, double OldestHours);

    private static string Describe(WaitingWork w)
    {
        var parts = new List<string>();
        if (w.Mail > 0) parts.Add($"{w.Mail} message(s)");
        if (w.Tasks > 0) parts.Add($"{w.Tasks} task(s)");
        if (parts.Count == 0) return "nothing";
        // The label belongs in the description, not in a detail view: a log
        // line reading "4 message(s)" while the agent it spawned keeps finding
        // an empty inbox is the exact report that hid this bug for three days.
        return string.Join(" and ", parts)
             + (w.Works.Count > 0 ? $" [work: {string.Join(", ", w.Works)}]" : "")
             + (w.OldestHours >= 1 ? $" [oldest {AgeLabel(w.OldestHours)}]" : "");
    }

    private static string AgeLabel(double hours) =>
        hours < 48 ? $"{(int)hours}h" : $"{(int)(hours / 24)}d";

    /// <summary>
    /// Every agent that has something waiting: anyone with an inbox directory,
    /// anyone named as an assignee on an open task, and anyone who has ever
    /// heartbeated. The union matters — an agent that has never connected can
    /// still be the assignee of a task, and that is exactly the case the owner
    /// has to be told about rather than have silently skipped.
    /// </summary>
    private static List<string> AgentsWithWaitingWork()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var root = Path.Combine(BusRoot, "inbox");
            if (Directory.Exists(root))
                foreach (var d in Directory.GetDirectories(root))
                    if (Directory.GetFiles(d, "*.json").Length > 0)
                        set.Add(Path.GetFileName(d));
        }
        catch { }

        try
        {
            if (Directory.Exists(HandoffDir))
                foreach (var f in new DirectoryInfo(HandoffDir).GetFiles("*.md"))
                {
                    var head = ReadHead(f.FullName, 2048);
                    if (head == null) continue;
                    var fm = ParseFrontmatter(head);
                    if (!string.Equals(fm.GetValueOrDefault("status", "open"), "open", StringComparison.OrdinalIgnoreCase)) continue;
                    var a = fm.GetValueOrDefault("assignee", "");
                    if (!string.IsNullOrWhiteSpace(a) && !a.Equals("any", StringComparison.OrdinalIgnoreCase))
                        set.Add(CollapseToReadableBox(SanitizeAgentSlug(a)));
                }
        }
        catch { }

        // The broker is not an agent and must never be spawned as one.
        set.Remove("broker");
        return set.ToList();
    }

    private static WaitingWork WaitingWorkFor(string agent)
    {
        var mail = 0;
        var oldest = 0.0;
        var works = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Same both-boxes rule the wake hook uses: the bus collapses every
            // Claude session into "claude", so counting only the fine name
            // misses the mail that actually arrives.
            foreach (var box in BoxesFor(agent))
            {
                var dir = Path.Combine(BusRoot, "inbox", box);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    mail++;
                    try
                    {
                        var age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalHours;
                        if (age > oldest) oldest = age;
                    }
                    catch { }
                    // Opening the file is the price of knowing the label. The
                    // dashboard counts from filenames alone and is right to —
                    // it only ever needs a number. A spawn needs the label.
                    try
                    {
                        var w = JObject.Parse(File.ReadAllText(f))["work"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(w)) works.Add(w!);
                    }
                    catch { /* half-written message: next tick's problem */ }
                }
            }
        }
        catch { }

        var tasks = 0;
        try
        {
            var open = OpenTasksForWake(agent);
            tasks = open.Count;
            foreach (var t in open) if (t.AgeHours > oldest) oldest = t.AgeHours;
        }
        catch { }
        return new WaitingWork(mail, tasks, works.ToList(), oldest);
    }

    private static IEnumerable<string> BoxesFor(string agent)
    {
        yield return agent;
        var dash = agent.IndexOf('-');
        if (dash > 0) yield return agent[..dash];
    }

    // ───────────── per-agent state ─────────────

    private sealed class RunnerState
    {
        public long? LastCalls { get; set; }
        public DateTime? CallsStillSince { get; set; }
        public List<DateTime> Spawns { get; set; } = new();
        public int Hops { get; set; }

        /// <summary>
        /// The child this agent is currently running, on disk rather than only
        /// in the broker's memory.
        ///
        /// The in-memory guard is correct for a broker that stays up and
        /// useless for every other case: <c>--once</c> is a whole separate
        /// PROCESS per tick, so it never saw its own previous spawn and
        /// started a second agent on the same consume-on-read inbox — the one
        /// race the bus exists to prevent. A crash or a service restart mid-run
        /// had the same hole.
        ///
        /// Start time is stored with the pid because Windows reuses pids; a
        /// bare pid check would eventually match some unrelated process and
        /// leave an agent permanently "busy".
        /// </summary>
        public int? RunPid { get; set; }
        public DateTime? RunStartedUtc { get; set; }
    }

    private static string RunnerStatePath(string agent) => Path.Combine(BrokerDir, agent + ".state.json");

    private static RunnerState ReadRunnerState(string agent)
    {
        try
        {
            var p = RunnerStatePath(agent);
            if (!File.Exists(p)) return new RunnerState();
            var o = JObject.Parse(File.ReadAllText(p));
            return new RunnerState
            {
                LastCalls = o["lastCalls"]?.ToObject<long?>(),
                CallsStillSince = o["callsStillSince"]?.ToObject<DateTime?>(),
                Hops = o["hops"]?.ToObject<int?>() ?? 0,
                Spawns = o["spawns"]?.ToObject<List<DateTime>>() ?? new List<DateTime>(),
                RunPid = o["runPid"]?.ToObject<int?>(),
                RunStartedUtc = o["runStartedUtc"]?.ToObject<DateTime?>(),
            };
        }
        catch { return new RunnerState(); }
    }

    private static void SaveRunnerState(string agent, RunnerState s)
    {
        try
        {
            Directory.CreateDirectory(BrokerDir);
            AtomicWriteJson(RunnerStatePath(agent), new JObject
            {
                ["lastCalls"] = s.LastCalls,
                ["callsStillSince"] = s.CallsStillSince,
                ["hops"] = s.Hops,
                ["spawns"] = JArray.FromObject(s.Spawns),
                ["runPid"] = s.RunPid,
                ["runStartedUtc"] = s.RunStartedUtc,
            });
        }
        catch { }
    }

    // ───────────── config ─────────────

    private sealed class RunnerSpec
    {
        public string Exe { get; init; } = "";
        public List<string> ExeFallbacks { get; init; } = new();
        public List<string> Args { get; init; } = new();
        public string Cwd { get; init; } = "";
    }

    private sealed class BrokerConfig
    {
        public int PollSeconds { get; init; } = 15;
        public int IdleGraceSeconds { get; init; } = 45;
        public int MaxRunSeconds { get; init; } = 900;
        public int MaxHopsPerWork { get; init; } = 12;
        public int MaxSpawnsPerHour { get; init; } = 20;

        /// <summary>
        /// How old waiting work has to be before a BUSY agent is told about it
        /// anyway. Ten minutes: long enough that a message and its reply in the
        /// same conversation never trip it, short enough that nothing spends a
        /// working day unread.
        /// </summary>
        public int StaleMailMinutes { get; init; } = 10;

        /// <summary>
        /// How long a parked agent is given to answer a nudge before the
        /// broker stops believing it can. Generous, because a spawn costs real
        /// money and a session that IS about to look is the cheaper outcome.
        /// </summary>
        public int ParkedSpawnAfterMinutes { get; init; } = 20;

        /// <summary>Work label to the folder that work lives in.</summary>
        public Dictionary<string, string> WorkDirs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RunnerSpec> Runners { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public JObject Escalation { get; init; } = new();
    }

    /// <summary>
    /// Read runners.json, writing a documented default first if it is missing.
    /// The default deliberately ships the two runners this machine can
    /// actually start, with the flags that are known to work — a config file
    /// whose examples do not run teaches the owner nothing.
    /// </summary>
    private static BrokerConfig LoadBrokerConfig()
    {
        if (!File.Exists(BrokerConfigPath)) WriteDefaultBrokerConfig();

        JObject o;
        try { o = JObject.Parse(File.ReadAllText(BrokerConfigPath)); }
        catch (Exception ex)
        {
            // Fail loud, not open. A broker running on silently-empty config
            // spawns nothing and looks exactly like a broker with no work.
            BrokerLog("runners.json is not valid JSON — " + Redact(ex.Message) + " · running with no runners");
            o = new JObject();
        }

        var runners = new Dictionary<string, RunnerSpec>(StringComparer.OrdinalIgnoreCase);
        if (o["runners"] is JObject rs)
            foreach (var (name, val) in rs)
            {
                if (val is not JObject r) continue;
                runners[SanitizeAgentSlug(name)] = new RunnerSpec
                {
                    Exe = r["exe"]?.ToString() ?? "",
                    ExeFallbacks = r["exeFallbacks"]?.ToObject<List<string>>() ?? new(),
                    Args = r["args"]?.ToObject<List<string>>() ?? new(),
                    Cwd = Environment.ExpandEnvironmentVariables(r["cwd"]?.ToString() ?? ""),
                };
            }

        var workDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (o["workDirs"] is JObject wd)
            foreach (var (k, v) in wd)
            {
                if (k.StartsWith("//", StringComparison.Ordinal)) continue;
                var dir = Environment.ExpandEnvironmentVariables(v?.ToString() ?? "");
                if (dir.Length > 0) workDirs[k] = dir;
            }

        var b = o["budget"] as JObject ?? new JObject();
        return new BrokerConfig
        {
            PollSeconds = Math.Max(5, o["pollSeconds"]?.ToObject<int?>() ?? 15),
            IdleGraceSeconds = Math.Max(10, o["idleGraceSeconds"]?.ToObject<int?>() ?? 45),
            MaxRunSeconds = Math.Max(60, b["maxRunSeconds"]?.ToObject<int?>() ?? 900),
            MaxHopsPerWork = Math.Max(1, b["maxHopsPerWork"]?.ToObject<int?>() ?? 12),
            MaxSpawnsPerHour = Math.Max(1, b["maxSpawnsPerHour"]?.ToObject<int?>() ?? 20),
            StaleMailMinutes = Math.Max(1, o["staleMailMinutes"]?.ToObject<int?>() ?? 10),
            ParkedSpawnAfterMinutes = Math.Max(1, o["parkedSpawnAfterMinutes"]?.ToObject<int?>() ?? 20),
            WorkDirs = workDirs,
            Runners = runners,
            Escalation = o["escalation"] as JObject ?? new JObject(),
        };
    }

    private static void WriteDefaultBrokerConfig()
    {
        try
        {
            Directory.CreateDirectory(BusRoot);
            var json = """
{
  "//": "BrainX broker. One entry per agent that can be STARTED headless.",
  "//pollSeconds": "How often the queue is checked.",
  "//idleGraceSeconds": "A live session whose tool-call counter has not moved for this long is parked, not working.",
  "//budget": "Ceilings, because an autonomous loop with no ceiling is a bill.",
  "//staleMailMinutes": "Work older than this is put in front of a BUSY agent too. Busy never meant busy with THIS.",
  "pollSeconds": 15,
  "idleGraceSeconds": 45,
  "staleMailMinutes": 10,
  "//parkedSpawnAfterMinutes": "A parked session cannot be pushed to - the harness owns stdin. After this long, stop nudging and spawn a fresh headless run instead.",
  "parkedSpawnAfterMinutes": 20,

  "//workDirs": "Which folder each work label lives in. Unmapped labels are NEVER guessed - the broker asks you instead, because a headless agent in the wrong repo does real work in the wrong place.",
  "workDirs": {},

  "budget": { "maxRunSeconds": 900, "maxHopsPerWork": 12, "maxSpawnsPerHour": 20 },

  "//escalation": "Where a question for the OWNER goes. Telegram token is read from the BRAINX_TELEGRAM_TOKEN env var if left blank here.",
  "escalation": {
    "telegram": { "botToken": "", "chatId": "" },
    "toast": true,
    "chatCard": true
  },

  "//runners": "{prompt} and {cwd} are substituted. NOTHING from a peer message is ever interpolated.",
  "runners": {
    "codex": {
      "//": "--approve-for-me is REQUIRED: headless Codex auto-denies every MCP tool call through its approval gate without it.",
      "exe": "codex",
      "exeFallbacks": ["%LOCALAPPDATA%\\OpenAI\\Codex\\bin\\*\\codex.exe"],
      "args": ["exec", "--skip-git-repo-check", "--approve-for-me", "-C", "{cwd}", "{prompt}"],
      "cwd": "D:\\BrainX"
    },
    "claude": {
      "exe": "claude",
      "exeFallbacks": [],
      "args": ["-p", "{prompt}"],
      "cwd": "D:\\BrainX"
    }
  }
}
""";
            File.WriteAllText(BrokerConfigPath, json, new UTF8Encoding(false));
            BrokerLog("wrote a default runners.json — edit it to add agents");
        }
        catch (Exception ex) { BrokerLog("could not write default runners.json — " + Redact(ex.Message)); }
    }

    // ───────────── logging ─────────────

    private static void BrokerLog(string line)
    {
        var stamped = $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {line}";
        // Only the CLI owns stdout. Inside the MCP server this same code runs
        // under agent_ask_user, where stdout is the JSON-RPC pipe and one
        // stray line is a corrupt frame.
        if (_brokerIsCli) Console.WriteLine(stamped);
        try
        {
            Directory.CreateDirectory(BrokerDir);
            File.AppendAllText(Path.Combine(BrokerDir, $"broker-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log"),
                               stamped + Environment.NewLine, new UTF8Encoding(false));
        }
        catch { }
    }
}
