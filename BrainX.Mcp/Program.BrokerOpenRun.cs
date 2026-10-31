using System.Collections.Concurrent;
using System.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// A room run that stays open for the owner's next line.
//
// Owner (2026-10-30): "ในห้อง cowrok room ทำไม มันชอบค้างกัน หลุดกันบ่อยมาก ไม่ค่อยจะตอบ
// อยากให้ตอบทันที ที่บอสพิพม์ ต้องรับรู้กันเร็วที่สุด".
//
// Every line used to cost a new process. Measured that morning: 40 to 64
// seconds from the spawn to the first word in the room — 22 to boot Claude Code
// and its MCP servers, 6 for the session hooks, a turn to load the deferred
// tools, a turn to join, a turn to speak — and the run that answered had never
// heard its own last answer.
//
// `claude -p` takes further turns on stdin under --input-format stream-json and
// costs nothing between them: no polling, no tokens, one idle process. So a room
// run on claude is left open for HoldOpenMinutes after it goes quiet, and the
// owner's next line reaches it within a second (BrokerNap). On a small model the
// second turn took 1.8 seconds against 4.5 for the first, and EOF on stdin ended
// the process with exit 0 in under a second.
//
// Nothing a peer wrote can get down that pipe: what is fed is always one of the
// constants below. The run reads the owner's words with cowork_read, from the
// sealed lines, like every other run.
//
// A line fed mid-turn waits for the tool that is running — stdin is read
// between turns too, but a tool call is not interrupted. That is why the floor
// rules forbid long blocking calls (rule 15), and why the room is told when an
// answer is late (CoworkTellWhyNoAnswerYet).
//
// Only claude: `codex exec` and `grok -p` take one prompt and leave. Those two
// still gain BrokerNap, and a cold start for each line.
//
// What is left of the wait is mostly the hooks in the owner's settings.json. Each
// one is a PowerShell, and powershell.exe alone takes 4 to 5 seconds to start on
// this machine. Measured 2026-10-30 on the same small model and the same two
// lines: first answer 60s with hooks and 7.5s without, the next line 68s and
// 7.7s, a tool call 11 to 17s and 0.1s. `roomHooks: false` is the switch. It is
// the owner's to throw, because those hooks are also how a run gets the repo's
// traps and a file's past lessons (see "Where things live" in their CLAUDE.md).
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    private const string OwnerSpokeFeed =
        "THE OWNER SPOKE IN THE COWORK ROOM just now. Call cowork_read and answer them with cowork_say before "
      + "anything else, even if it is only one line on where you are. Then carry on.";

    private const string FollowUpFeed =
        "WORK IN THE COWORK ROOM IS WAITING FOR YOU and nobody is moving it; the broker's line in the room to you "
      + "says which. Call cowork_read and cowork_task list, say in the room what you are picking up, and continue "
      + "it where it stopped.";

    private const string OpenRunNote =
        "This session stays connected when you finish: once you have answered and nothing is left, end your turn. "
      + "Do not wait, poll or start a watch to stay around. The owner's next line is handed to you here within a "
      + "second, with everything from this session still in front of you. ";

    /// <summary>How long a closed run gets to leave by itself. A turn that was
    /// only just ending can take a moment; a process that ignores EOF cannot
    /// be left holding the agent's slot.</summary>
    private static readonly TimeSpan OpenRunCloseGrace = TimeSpan.FromSeconds(15);

    /// <summary>Mail has no lane into a room run, and every tick an open run
    /// sits there is a tick that mail is not served. Past this it makes way.</summary>
    private static readonly TimeSpan OpenRunYieldsToMail = TimeSpan.FromMinutes(5);

    /// <summary>False under --once and --dry-run. A one-tick broker waits for
    /// its runs to end (SuperviseToCompletion), and an open run does not.</summary>
    private static bool _brokerStaysUp;

    /// <summary>What the broker knows about an open run, from its event stream.
    /// Written on the pipe reader's thread, read on the broker's.</summary>
    private sealed class OpenRun
    {
        private readonly object _gate = new();
        private readonly BlockingCollection<string> _input = new();
        private DateTime _lastEventUtc = DateTime.UtcNow;
        private DateTime _lastFedUtc = DateTime.UtcNow;
        private bool _busy = true;
        private string? _error;
        private string? _tool;
        private DateTime _toolUtc;

        public required string? Model { get; init; }
        public required string? Effort { get; init; }

        /// <summary>When the broker shut its stdin. Broker thread only.</summary>
        public DateTime? ClosedUtc { get; set; }

        /// <summary>What the state file says, so it is only written when that
        /// changes. Broker thread only.</summary>
        public bool ToldQuiet { get; set; }

        /// <summary>A turn is going. False between the end of one and the next line.</summary>
        public bool Busy { get { lock (_gate) return _busy; } }
        public DateTime LastEventUtc { get { lock (_gate) return _lastEventUtc; } }
        public DateTime LastFedUtc { get { lock (_gate) return _lastFedUtc; } }
        /// <summary>What a turn that failed said. Such a session is finished:
        /// the next line goes to a new one.</summary>
        public string? Error { get { lock (_gate) return _error; } }
        public (string? Name, DateTime SinceUtc) Tool { get { lock (_gate) return (_tool, _toolUtc); } }

        /// <summary>
        /// Its own thread, because a write to a pipe waits for the reader once
        /// the pipe is full, and the pipe holds 4 KB. The prompt is bigger than
        /// that and claude takes half a minute to start reading, so a write on
        /// the broker's thread held up every other agent until it had booted.
        /// Found by running this against the real CLI: the stand-in reads at once.
        /// </summary>
        public void PumpInto(StreamWriter stdin)
        {
            new Thread(() =>
            {
                try
                {
                    foreach (var line in _input.GetConsumingEnumerable())
                    {
                        stdin.Write(line);
                        stdin.Write('\n');
                        stdin.Flush();
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // The run has gone. The reaper says how it ended.
                }
                try { stdin.Close(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
            }) { IsBackground = true, Name = "open-run-stdin" }.Start();
        }

        public void Send(string line)
        {
            _input.Add(line);
            lock (_gate) { _busy = true; _lastFedUtc = _lastEventUtc = DateTime.UtcNow; }
        }

        /// <summary>What is queued goes in, and then EOF.</summary>
        public void EndInput() => _input.CompleteAdding();

        public void Working(string? tool)
        {
            lock (_gate)
            {
                _busy = true;
                _lastEventUtc = DateTime.UtcNow;
                if (tool != null && tool != _tool) { _tool = tool; _toolUtc = _lastEventUtc; }
            }
        }

        public void ToolDone() { lock (_gate) { _tool = null; _lastEventUtc = DateTime.UtcNow; } }

        public void TurnEnded(string? error)
        {
            lock (_gate)
            {
                _busy = false;
                _tool = null;
                _lastEventUtc = DateTime.UtcNow;
                _error ??= error;
            }
        }
    }

    /// <summary>
    /// The args that keep this run open, or null to run it the old way: `-p
    /// {prompt}` becomes `-p` plus the stream flags, and the prompt goes in on
    /// stdin. Claude by default; `holdOpen` in runners.json says otherwise, and
    /// args without a `-p {prompt}` to replace are left alone.
    /// </summary>
    private static List<string>? OpenRunArgs(BrokerConfig cfg, string exe, RunnerSpec runner, IReadOnlyList<string> args)
    {
        if (!_brokerStaysUp || cfg.HoldOpenMinutes <= 0) return null;
        if (!(runner.HoldOpen ?? Path.GetFileNameWithoutExtension(exe).Equals("claude", StringComparison.OrdinalIgnoreCase)))
            return null;
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] is not ("-p" or "--print") || args[i + 1] != "{prompt}") continue;
            var open = args.ToList();
            open.RemoveAt(i + 1);
            open.InsertRange(i + 1, new[] { "--input-format", "stream-json", "--output-format", "stream-json", "--verbose" });
            if (!cfg.RoomHooks) open.InsertRange(i + 1, new[] { "--settings", "{\"disableAllHooks\":true}" });
            return open;
        }
        return null;
    }

    /// <summary>
    /// One line of an open run's output. The run log keeps what a plain `-p`
    /// run printed, the final text of each turn. The stream also carries every
    /// tool's input and output, and that does not belong in a file in the
    /// synced vault.
    /// </summary>
    private static void OpenRunOutput(OpenRun open, string log, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        JObject o;
        try { o = JObject.Parse(line); }
        catch { AppendRunLog(log, line); return; }

        switch (o["type"]?.ToString())
        {
            case "result":
                var text = o["result"]?.ToString();
                if (!string.IsNullOrWhiteSpace(text)) AppendRunLog(log, text);
                open.TurnEnded(o["is_error"]?.ToObject<bool?>() == true
                    ? (string.IsNullOrWhiteSpace(text) ? o["subtype"]?.ToString() ?? "error" : text)
                    : null);
                break;
            case "assistant":
                open.Working(ToolNameIn(o));
                break;
            case "user":
                open.ToolDone();
                break;
            case "system" when o["subtype"]?.ToString() == "init":
                open.Working(null);
                break;
        }
    }

    /// <summary>The tool this message calls, as a word safe to say in the room:
    /// the name comes from the model, and it is going on the wall.</summary>
    private static string? ToolNameIn(JObject message)
    {
        var name = (message["message"]?["content"] as JArray)?
            .LastOrDefault(b => b["type"]?.ToString() == "tool_use")?["name"]?.ToString();
        if (string.IsNullOrEmpty(name)) return null;
        var word = new string(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(48).ToArray());
        return word.Length > 0 ? word : null;
    }

    /// <summary>Hand an open run its next turn. False when it cannot take one.</summary>
    private static bool FeedOpenRun(BrokerRun run, string text)
    {
        if (run.Open is not { ClosedUtc: null } open || run.Process.HasExited) return false;
        open.Send(new JObject
        {
            ["type"] = "user",
            ["message"] = new JObject { ["role"] = "user", ["content"] = text },
        }.ToString(Formatting.None));
        return true;
    }

    /// <summary>Shut stdin: the run finishes what it is on and leaves.</summary>
    private static void CloseOpenRun(string agent, BrokerRun run, string why)
    {
        if (run.Open is not { ClosedUtc: null } open) return;
        open.ClosedUtc = DateTime.UtcNow;
        open.EndInput();
        BrokerLog($"{agent}: closing the open run — {why}");
    }

    /// <summary>Is there a reason for this open run to go now? Checked every
    /// tick. Only between turns, except for a turn that failed: closing a run
    /// that is working would cut the owner's job off.</summary>
    private static void SettleOpenRun(BrokerConfig cfg, string agent, BrokerRun run)
    {
        if (run.Open is not { } open) return;
        var now = DateTime.UtcNow;

        // The room shows what the owner's runs are doing, and one with nothing
        // to do is not to be read as one working.
        if (open.Busy == open.ToldQuiet)
        {
            open.ToldQuiet = !open.Busy;
            var st = ReadRunnerState(agent);
            st.RunQuiet = open.ToldQuiet;
            SaveRunnerState(agent, st);
        }

        if (open.ClosedUtc is DateTime closed)
        {
            if (now - closed >= OpenRunCloseGrace) TryKill(run.Process);
            return;
        }
        if (open.Error != null) { CloseOpenRun(agent, run, "a turn ended in an error"); return; }
        if (open.Busy) return;

        // Under the run ceiling, or that would kill it first and report it as
        // a run that took too long.
        var hold = TimeSpan.FromMinutes(Math.Min(cfg.HoldOpenMinutes, cfg.MaxRunSeconds / 60.0 - 1));
        if (now - open.LastEventUtc >= hold)
            CloseOpenRun(agent, run, $"quiet for {hold.TotalMinutes:0.#} minutes");
        else if (!CoworkRoomIsOpen())
            CloseOpenRun(agent, run, "the room went dark");
        else if (cfg.Runners.TryGetValue(agent, out var runner)
                 && (!string.Equals(RunnerModelFor(agent, runner), open.Model, StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(RunnerEffortFor(agent, runner, open.Model), open.Effort, StringComparison.OrdinalIgnoreCase)))
            CloseOpenRun(agent, run, "the owner picked another model or effort, and the next line goes to that");
    }

    /// <summary>
    /// An agent with a run going, and something waiting for it. True unless an
    /// owner's line could not be handed over and has to be read again next tick.
    /// </summary>
    private static bool CoworkReachLiveRun(BrokerConfig cfg, string agent, BrokerRun run, bool called, List<string>? chased)
    {
        if (called) run.OwnerWaitingSinceUtc ??= DateTime.UtcNow;
        if (run.Open is not { } open) return true;

        if (called)
        {
            var text = OwnerSpokeFeed;
            var answers = WaitingWorkFor(agent).Answers;
            if (answers is { Count: > 0 }) text += " " + OwnerAnsweredNote(answers);
            return FeedOpenRun(run, text);
        }

        if (open.Busy) return true;

        // The broker chasing work is not the owner speaking, so it stays under
        // the ceilings a spawn is under: a follow-up that goes down this pipe
        // must not become the loop those exist to stop.
        if (chased is { Count: > 0 })
        {
            var st = ReadRunnerState(agent);
            if (BudgetGate(cfg, agent, st) != null) return true;
            if (!FeedOpenRun(run, FollowUpFeed)) return true;
            st.Hops++;
            st.Spawns.Add(DateTime.UtcNow);
            SaveRunnerState(agent, st);
            return true;
        }

        if (DateTime.UtcNow - open.LastEventUtc >= OpenRunYieldsToMail
            && WaitingWorkFor(agent) is { } work && work.Mail + work.Tasks > 0)
            CloseOpenRun(agent, run, "mail is waiting for a run of its own");
        return true;
    }

    /// <summary>
    /// A run that has not answered the owner a minute on says why, once per
    /// line. The owner wrote "อยู่ไหม" at 13:36 that day, into a room that showed
    /// nothing while claude waited on a build.
    /// </summary>
    private static void CoworkTellWhyNoAnswerYet(BrokerConfig cfg, Dictionary<string, BrokerRun> live)
    {
        foreach (var (agent, run) in live)
        {
            if (run.OwnerWaitingSinceUtc is not DateTime since) continue;
            if (CoworkSpokeSince(agent, since)) { run.OwnerWaitingSinceUtc = null; continue; }
            if ((DateTime.UtcNow - since).TotalSeconds < cfg.OwnerWaitSeconds) continue;
            run.OwnerWaitingSinceUtc = null;

            var (tool, toolUtc) = run.Open?.Tool ?? (null, default);
            CoworkSystemLine(tool != null
                ? $"⏳ ข้อความถึง {agent} แล้ว แต่ยังรอ {tool} ที่รันมา {SpanTh(DateTime.UtcNow - toolUtc)} อยู่ — จบแล้วจะตอบทันที"
                : $"⏳ ข้อความถึง {agent} แล้ว ตอนนี้กำลังคิดหรือทำขั้นตอนหนึ่งอยู่ — จบขั้นตอนนี้แล้วจะตอบทันที");
        }
    }

    private static string SpanTh(TimeSpan t) =>
        t.TotalSeconds < 90 ? $"{t.TotalSeconds:F0} วินาที" : $"{t.TotalMinutes:F0} นาที";

    /// <summary>
    /// The wait between ticks, cut short by what cannot wait. The poll is still
    /// what drives the broker; this only ends it early. A line used to sit for
    /// up to pollSeconds before anything looked at it, and a run that died sat
    /// the same way before the room heard why. <paramref name="heard"/> is the
    /// newest owner line from before the tick read the room.
    /// </summary>
    private static async Task BrokerNap(BrokerConfig cfg, Dictionary<string, BrokerRun> live, string heard, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(cfg.PollSeconds);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            if (NewestOwnerLine() != heard) return;
            if (live.Values.Any(r => r.Process.HasExited || r.Open is { Error: not null, ClosedUtc: null })) return;
        }
    }

    /// <summary>
    /// A tick still busy after it has read the room, for the check that has to
    /// land a line there. Sandbox only: real ticks do this by themselves, a
    /// Telegram send inside one can take twenty seconds.
    /// </summary>
    private static async Task SandboxTickHold(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("BRAINX_SANDBOX") != "1"
            || !int.TryParse(Environment.GetEnvironmentVariable("BRAINX_BROKER_TICK_HOLD_MS"), out var ms) || ms <= 0) return;
        BrokerLog($"tick held for {ms} ms");
        await Task.Delay(ms, ct).ConfigureAwait(false);
    }

    /// <summary>The newest line with the owner's name on the file. Only a nudge
    /// to look: whether it really is theirs is decided by the seal, in the tick.
    /// A forged file buys one early tick and nothing else.</summary>
    private static string NewestOwnerLine()
    {
        try
        {
            var newest = "";
            foreach (var f in Directory.EnumerateFiles(CoworkMessagesDir, "*-owner-*.json"))
                if (Path.GetFileName(f) is var n && string.CompareOrdinal(n, newest) > 0) newest = n;
            return newest;
        }
        catch { return ""; }
    }

    /// <summary>A refused run's work is not chased for this long, or until a run
    /// gets through. Follow-ups sent the same session back into the same refusal
    /// four times in twenty minutes on 2026-10-30. The owner's own line is not
    /// held back: new words are the one thing that can get past it.</summary>
    private static readonly TimeSpan FollowUpAfterRefusal = TimeSpan.FromMinutes(30);

    private static bool CoworkRefusedLately(string agent) =>
        ReadRunnerState(agent).RefusedUtc is DateTime r && DateTime.UtcNow - r < FollowUpAfterRefusal;
}
