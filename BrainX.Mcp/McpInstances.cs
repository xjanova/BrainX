// McpInstances.cs — knowing which brainx-mcp processes are still worth their RAM.
//
// A stdio MCP server is supposed to die when its client closes the pipe:
// Launcher.RunAsync reads stdin until EOF and takes the worker down with it.
// That contract holds for Claude Code. It does NOT hold for Claude Desktop,
// which logs "[LocalMcpServerManager] Closing brainx-brain" and then leaves the
// helper process that owns the pipe handle alive — so our stdin never reaches
// EOF, the launcher blocks on ReadLineAsync forever, and the pair leaks. On the
// owner's machine, 2026-08-23: 40 brainx-mcp processes, 20 launcher+worker
// pairs, 1,186 MB, the oldest 31 hours old. Every one of them was still holding
// an open SQLite handle on the vault.
//
// The OS cannot tell us which of those are abandoned. The pipe is open in both
// cases — attached-and-idle looks exactly like leaked, byte for byte. So we
// measure the one thing that does separate them: TRAFFIC. Every launcher writes
// a heartbeat record next to machine-settings.json and stamps it whenever the
// client says anything at all. Silence for longer than the threshold means
// nobody is on the other end.
//
// Two mechanisms, deliberately overlapping:
//
//   SELF-EXIT   a launcher that has been silent past the threshold closes
//               itself. It is judging its OWN idleness from memory, not from a
//               file that could be stale, so this is the one that should
//               normally fire. Cheapest and safest.
//   SWEEP       a launcher, at boot, reaps records that are past the threshold.
//               Covers what self-exit cannot: a launcher killed mid-life, a
//               machine that slept through its own timer, and (with
//               includeUnknown) the pile left behind by builds that predate
//               this file.
//
// Rules that keep the sweep from becoming the thing it is cleaning up after:
//   • Only processes named brainx-mcp. Never anything else, ever.
//   • Never this process, never its worker.
//   • pid + process start time must BOTH match the record. A pid alone is a
//     number Windows hands out again five minutes later.
//   • Kill LAUNCHERS only, one process at a time, never entireProcessTree.
//     A worker whose launcher dies gets EOF on stdin and exits on its own —
//     and a tree kill here is precisely the bug that took the GUI down.
//   • A process younger than the grace period is never touched: it may simply
//     not have registered yet.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

internal static class McpInstances
{
    /// <summary>Beside machine-settings.json and vault-path.txt: these are
    /// facts about THIS MACHINE's processes, not about the knowledge base. A
    /// vault on a synced drive must never carry another PC's pid table.</summary>
    private static string InstancesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrainX", "mcp-instances");

    private static string MachineSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrainX", "machine-settings.json");

    private static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SettingsTtl = TimeSpan.FromSeconds(60);

    /// <summary>Nothing this young is ever reaped. It covers the window between
    /// Process.Start and the new launcher writing its first record — without
    /// it, two launchers booting together could reap each other.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(5);

    public const double DefaultIdleHours = 6.0;

    // ── this process's own record ────────────────────────────────────

    private static readonly object Gate = new();
    private static string _file = "";
    private static JObject _rec = new();
    private static DateTime _lastFlushUtc = DateTime.MinValue;
    private static DateTime _lastActivityUtc = DateTime.UtcNow;
    private static int _workerPid;
    private static bool _registered;
    /// <summary>Our parent's start time at registration — the half of its
    /// identity that a recycled pid cannot forge. See ParentGone.</summary>
    private static DateTime? _parentStartUtc;

    public static TimeSpan IdleFor
    {
        get { lock (Gate) return DateTime.UtcNow - _lastActivityUtc; }
    }

    /// <summary>Called once by the launcher, before it spawns anything.</summary>
    public static void Register(string vault)
    {
        try
        {
            Directory.CreateDirectory(InstancesDir);
            _file = Path.Combine(InstancesDir, Environment.ProcessId + ".json");
            using var me = Process.GetCurrentProcess();

            var ppid = ParentPid(Environment.ProcessId);
            DateTime? parentStart = null;
            if (ppid > 0)
                try { using var par = Process.GetProcessById(ppid); parentStart = StartTimeUtc(par); }
                catch { }

            lock (Gate)
            {
                _parentStartUtc = parentStart;
                _lastActivityUtc = DateTime.UtcNow;
                _rec = new JObject
                {
                    ["pid"] = Environment.ProcessId,
                    // Identity, not decoration: a record whose start time no
                    // longer matches its pid belongs to a process that died and
                    // whose number was recycled. See LooksLikeSameProcess.
                    ["startedUtc"] = Iso(StartTimeUtc(me) ?? DateTime.UtcNow),
                    ["workerPid"] = 0,
                    ["parentPid"] = ppid,
                    ["parentStartedUtc"] = parentStart is { } ps ? Iso(ps) : "",
                    ["exe"] = Environment.ProcessPath ?? "",
                    ["vault"] = vault ?? "",
                    ["client"] = "",
                    ["bootUtc"] = Iso(DateTime.UtcNow),
                    ["lastActivityUtc"] = Iso(_lastActivityUtc),
                };
                _registered = true;
            }
            Flush(force: true);
        }
        catch { /* housekeeping must never take the server down with it */ }
    }

    public static void SetWorker(int pid)
    {
        lock (Gate) { _workerPid = pid; if (_registered) _rec["workerPid"] = pid; }
        Flush(force: true);
    }

    /// <summary>Whatever the client called itself in the initialize handshake —
    /// so `brainx-mcp reap --dry-run` reads like a list of applications rather
    /// than a list of pids.</summary>
    public static void SetClient(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (Gate) { if (_registered) _rec["client"] = name.Trim(); }
        Flush(force: true);
    }

    /// <summary>One line arrived from the client. Cheap on purpose: the clock
    /// moves in memory on every call, the file at most twice a minute.</summary>
    public static void Touch()
    {
        bool due;
        lock (Gate)
        {
            _lastActivityUtc = DateTime.UtcNow;
            due = _lastActivityUtc - _lastFlushUtc >= FlushEvery;
        }
        if (due) Flush(force: false);
    }

    /// <summary>
    /// True when the process that spawned us no longer exists.
    ///
    /// The unambiguous half of "unused": a launcher whose client process is
    /// gone is serving nobody and no threshold is needed to say so. Idle time
    /// is a judgement call; this is a fact.
    ///
    /// Windows does not reparent orphans and it reissues pids, so a dead
    /// parent's number can belong to something else entirely by the time we
    /// look. The start time we recorded at registration is what makes the
    /// answer trustworthy — and when it cannot be checked we answer "not
    /// gone", because the cost of a false positive here is closing a live
    /// session's brain.
    /// </summary>
    public static bool ParentGone()
    {
        int ppid;
        DateTime? claimed;
        lock (Gate)
        {
            if (!_registered) return false;
            ppid = _rec["parentPid"]?.Value<int>() ?? 0;
            claimed = _parentStartUtc;
        }
        if (ppid <= 0) return false;

        try
        {
            using var p = Process.GetProcessById(ppid);
            if (p.HasExited) return true;
            var started = StartTimeUtc(p);
            // Same pid, different process: our parent died and Windows handed
            // the number on.
            if (claimed is { } want && started is { } got)
                return Math.Abs((want - got).TotalSeconds) > 2;
            return false;
        }
        catch (ArgumentException) { return true; }   // no such process
        catch { return false; }                      // can't tell — assume attached
    }

    /// <summary>
    /// The machine was asleep (or hibernated, or the process was frozen) for
    /// <paramref name="gap"/>. Push the activity clock forward by the same
    /// amount so that time does not count as silence.
    ///
    /// Without this, closing the lid at 18:00 and opening it at 09:00 retires
    /// every MCP server on the machine during the first watch tick after
    /// resume — the owner would come back to a full desktop of clients whose
    /// brain had just disconnected, all at once, for no reason they could see.
    /// Idle means "awake and unspoken to".
    /// </summary>
    public static void NoteSleepGap(TimeSpan gap)
    {
        if (gap <= TimeSpan.Zero) return;
        lock (Gate)
        {
            var moved = _lastActivityUtc + gap;
            _lastActivityUtc = moved > DateTime.UtcNow ? DateTime.UtcNow : moved;
        }
        Flush(force: true);
    }

    public static void Unregister()
    {
        try { if (_file.Length > 0) File.Delete(_file); } catch { }
    }

    private static void Flush(bool force)
    {
        try
        {
            string json;
            lock (Gate)
            {
                if (!_registered || _file.Length == 0) return;
                if (!force && DateTime.UtcNow - _lastFlushUtc < FlushEvery) return;
                _rec["lastActivityUtc"] = Iso(_lastActivityUtc);
                _lastFlushUtc = DateTime.UtcNow;
                json = _rec.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            // tmp-then-move: a torn record reads as "unknown", and an unknown
            // record is one the sweep is allowed to kill on --legacy.
            var tmp = _file + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, _file, overwrite: true);
        }
        catch { }
    }

    // ── settings ─────────────────────────────────────────────────────

    private static JObject _settings = new();
    private static DateTime _settingsAt = DateTime.MinValue;

    /// <summary>Re-read on a TTL rather than cached for the process lifetime:
    /// a launcher can live for days, and un-ticking the box in Settings has to
    /// take effect without the owner hunting down every running session.</summary>
    private static JObject Settings()
    {
        if (DateTime.UtcNow - _settingsAt < SettingsTtl) return _settings;
        _settingsAt = DateTime.UtcNow;
        try
        {
            _settings = File.Exists(MachineSettingsPath)
                ? JObject.Parse(File.ReadAllText(MachineSettingsPath))
                : new JObject();
        }
        catch { _settings = new JObject(); }   // corrupt file = defaults, never a crash
        return _settings;
    }

    /// <summary>Default ON. Off is a deliberate choice the owner ticks; a
    /// missing key is a machine that has never seen the setting, and leaking
    /// a gigabyte quietly is the worse default.</summary>
    public static bool ReaperEnabled
    {
        get
        {
            try
            {
                return Settings()["McpReaperEnabled"] is { Type: JTokenType.Boolean } b
                    ? b.Value<bool>() : true;
            }
            catch { return true; }
        }
    }

    public static TimeSpan IdleThreshold
    {
        get
        {
            var hours = DefaultIdleHours;
            try
            {
                var t = Settings()["McpReaperIdleHours"];
                if (t is { Type: JTokenType.Integer } or { Type: JTokenType.Float })
                    hours = t.Value<double>();
            }
            catch { }
            // Clamped so a hand-edited 0 cannot turn the feature into a machine
            // that kills every server the moment it stops talking.
            if (hours < 0.5) hours = 0.5;
            if (hours > 24 * 14) hours = 24 * 14;
            return TimeSpan.FromHours(hours);
        }
    }

    // ── what is out there ────────────────────────────────────────────

    internal enum Verdict { Self, Worker, Active, Idle, Orphan, Unknown, TooYoung }

    internal sealed class Instance
    {
        public int Pid;
        public int ParentPid;
        public bool IsWorker;            // parent is another brainx-mcp
        public DateTime? StartedUtc;
        public DateTime? LastActivityUtc;
        public string Client = "";
        public long WorkingSet;
        /// <summary>This process plus the worker it owns. Closing a launcher
        /// takes its worker with it, and the worker is where the RAM actually
        /// is (7 MB launcher, 25-150 MB worker) — reporting only the launcher
        /// would undersell the saving by an order of magnitude.</summary>
        public long PairBytes;
        /// <summary>The process that spawned this launcher is gone.</summary>
        public bool ClientGone;
        public Verdict Verdict;

        public TimeSpan? Idle => LastActivityUtc is { } t ? DateTime.UtcNow - t : null;
        public TimeSpan? Age => StartedUtc is { } t ? DateTime.UtcNow - t : null;
    }

    /// <summary>
    /// Every brainx-mcp on this machine, classified. Read-only — the CLI prints
    /// it, the sweep acts on it, and both agree because they share this.
    /// </summary>
    internal static List<Instance> Scan()
    {
        var list = new List<Instance>();
        var parents = ParentMap();
        var records = LoadRecords();
        var mine = Environment.ProcessId;
        int worker;
        lock (Gate) worker = _workerPid;

        var procs = SafeGetProcesses();
        var mcpPids = new HashSet<int>(procs.Select(p => p.Id));

        foreach (var p in procs)
        {
            var inst = new Instance
            {
                Pid = p.Id,
                ParentPid = parents.TryGetValue(p.Id, out var pp) ? pp : 0,
                StartedUtc = StartTimeUtc(p),
            };
            inst.IsWorker = inst.ParentPid > 0 && mcpPids.Contains(inst.ParentPid);
            try { inst.WorkingSet = p.WorkingSet64; } catch { }

            if (records.TryGetValue(p.Id, out var rec) && LooksLikeSameProcess(rec, inst.StartedUtc))
            {
                inst.LastActivityUtc = ParseIso(rec["lastActivityUtc"]?.ToString());
                inst.Client = rec["client"]?.ToString() ?? "";
                inst.ClientGone = IsGone(rec["parentPid"]?.Value<int>() ?? 0,
                                         ParseIso(rec["parentStartedUtc"]?.ToString()));
            }

            inst.Verdict =
                  p.Id == mine                                  ? Verdict.Self
                : p.Id == worker && worker != 0                 ? Verdict.Worker
                : inst.Age is { } a && a < Grace                ? Verdict.TooYoung
                // Checked before idleness on purpose: "the client process no
                // longer exists" is a fact, and a fact outranks a timer.
                : inst.ClientGone                               ? Verdict.Orphan
                : inst.LastActivityUtc is null                  ? Verdict.Unknown
                : inst.Idle >= IdleThreshold                    ? Verdict.Idle
                :                                                 Verdict.Active;

            list.Add(inst);
            p.Dispose();
        }

        foreach (var i in list)
            i.PairBytes = i.WorkingSet + list.Where(w => w.IsWorker && w.ParentPid == i.Pid)
                                             .Sum(w => w.WorkingSet);

        return list.OrderBy(i => i.StartedUtc ?? DateTime.MaxValue).ToList();
    }

    internal sealed class SweepResult
    {
        public int Reaped;
        public long FreedBytes;
        public int Skipped;
        public int RecordsPruned;
        public List<string> Lines = new();
    }

    /// <summary>
    /// Close the launchers nobody is talking to.
    ///
    /// <paramref name="reapUnknownOlderThan"/> is null for the automatic sweep,
    /// and that is the whole safety story: automatically we touch ONLY servers
    /// whose own heartbeat proves them idle. Never a guess.
    ///
    /// A non-null value opts into the servers that have no heartbeat at all —
    /// which today means "started by a build from before this file existed".
    /// Those cannot be proven either way, so the only defensible rule left is
    /// age, and the caller has to name it. It matters: when this shipped, the
    /// owner's machine had 21 heartbeat-less pairs, and six of them were live
    /// sessions (one was the session doing the reaping). Everything live was
    /// under 11 hours old and everything abandoned was over 12 — age was the
    /// line, and a blanket "kill all unknowns" would have cut the brain out
    /// from under four working Claude sessions.
    /// </summary>
    internal static SweepResult Sweep(bool dryRun, TimeSpan? reapUnknownOlderThan, Action<string>? log = null)
    {
        var result = new SweepResult();

        // One sweeper at a time. Several clients starting together is the
        // normal case, and three launchers reaping the same pids would spend
        // their boot racing each other for corpses.
        Mutex? gate = null;
        var held = false;
        try
        {
            try
            {
                gate = new Mutex(false, @"Global\BrainX.McpReaper.v1");
                held = gate.WaitOne(TimeSpan.FromMilliseconds(750));
            }
            catch (AbandonedMutexException) { held = true; }
            catch { held = true; }   // no named mutexes here (non-Windows) — proceed
            if (!held) return result;

            result.RecordsPruned = PruneDeadRecords();

            var targets = new List<Instance>();
            foreach (var inst in Scan())
            {
                // Workers are never targeted directly. Killing one under a live
                // launcher just makes the launcher respawn it; killing the
                // launcher makes the worker leave on its own, through the same
                // stdin-EOF path it already handles.
                if (inst.IsWorker) { result.Skipped++; continue; }

                var oldEnough = reapUnknownOlderThan is { } cut
                             && inst.Age is { } age && age >= cut;
                var take = inst.Verdict is Verdict.Idle or Verdict.Orphan
                        || (inst.Verdict == Verdict.Unknown && oldEnough);

                if (take) { targets.Add(inst); continue; }

                result.Skipped++;
                if (inst.Verdict == Verdict.Unknown && reapUnknownOlderThan is { } c2)
                    log?.Invoke($"sparing pid {inst.Pid}: no heartbeat, but only "
                              + $"{Human(inst.Age ?? TimeSpan.Zero)} old (< {Human(c2)}) — "
                              + "assume a live session");
            }

            foreach (var t in targets)
            {
                var why = t.Verdict switch
                {
                    Verdict.Idle => $"idle {Human(t.Idle ?? TimeSpan.Zero)}",
                    Verdict.Orphan => "its client process is gone",
                    _ => $"no heartbeat, up {Human(t.Age ?? TimeSpan.Zero)}",
                };
                var who = t.Client.Length > 0 ? t.Client : "unknown client";
                var line = $"pid {t.Pid} · {who} · {why} · {t.PairBytes / (1024 * 1024)} MB (with its worker)";
                result.Lines.Add(line);
                log?.Invoke((dryRun ? "would reap " : "reaping ") + line);

                if (dryRun) { result.Reaped++; result.FreedBytes += t.PairBytes; continue; }
                if (KillLauncher(t.Pid, t.StartedUtc))
                {
                    result.Reaped++;
                    result.FreedBytes += t.PairBytes;
                    TryDeleteRecord(t.Pid);
                }
            }

            if (!dryRun && result.Reaped > 0) MopUpOrphanedWorkers(targets, log);
        }
        catch (Exception ex) { log?.Invoke($"sweep failed (non-fatal): {ex.Message}"); }
        finally
        {
            if (held) { try { gate?.ReleaseMutex(); } catch { } }
            gate?.Dispose();
        }

        return result;
    }

    /// <summary>
    /// One process, by pid, re-verified against its start time immediately
    /// before the kill. Never entireProcessTree: the GUI client used to be a
    /// child of a worker, and a tree kill here is what closed it out from under
    /// the owner on 2026-08-23.
    /// </summary>
    private static bool KillLauncher(int pid, DateTime? expectedStart)
    {
        if (pid == Environment.ProcessId) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            if (!p.ProcessName.Equals("brainx-mcp", StringComparison.OrdinalIgnoreCase)) return false;
            // The pid could have been recycled between Scan and here.
            var now = StartTimeUtc(p);
            if (expectedStart is { } want && now is { } got
                && Math.Abs((want - got).TotalSeconds) > 2) return false;
            p.Kill();
            p.WaitForExit(3000);
            return true;
        }
        catch { return false; }
    }

    /// <summary>A worker normally leaves when its launcher's pipe closes. Give
    /// it a moment, then insist — a worker with no launcher answers to nobody
    /// and still holds the vault's SQLite handles.</summary>
    private static void MopUpOrphanedWorkers(List<Instance> reaped, Action<string>? log)
    {
        var deadLaunchers = new HashSet<int>(reaped.Select(r => r.Pid));
        Thread.Sleep(1500);
        foreach (var p in SafeGetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                var parent = ParentPid(p.Id);
                if (!deadLaunchers.Contains(parent)) continue;
                if (p.HasExited) continue;
                log?.Invoke($"worker pid {p.Id} outlived its launcher — closing");
                p.Kill();
            }
            catch { }
            finally { p.Dispose(); }
        }
    }

    // ── records on disk ──────────────────────────────────────────────

    private static Dictionary<int, JObject> LoadRecords()
    {
        var map = new Dictionary<int, JObject>();
        try
        {
            if (!Directory.Exists(InstancesDir)) return map;
            foreach (var f in Directory.EnumerateFiles(InstancesDir, "*.json"))
            {
                try
                {
                    var o = ParseVerbatim(File.ReadAllText(f));
                    var pid = o["pid"]?.Value<int>() ?? 0;
                    if (pid > 0) map[pid] = o;
                }
                catch { /* torn or hand-edited — treat as no record */ }
            }
        }
        catch { }
        return map;
    }

    /// <summary>Records whose process is gone (or whose pid now belongs to
    /// somebody else) are deleted. Left alone, the directory becomes a slow
    /// leak of its own and every sweep re-reads more junk.</summary>
    private static int PruneDeadRecords()
    {
        var pruned = 0;
        foreach (var (pid, rec) in LoadRecords())
        {
            if (pid == Environment.ProcessId) continue;
            DateTime? started = null;
            var alive = false;
            try
            {
                using var p = Process.GetProcessById(pid);
                alive = !p.HasExited
                     && p.ProcessName.Equals("brainx-mcp", StringComparison.OrdinalIgnoreCase);
                started = StartTimeUtc(p);
            }
            catch { alive = false; }

            if (!alive) { if (TryDeleteRecord(pid)) pruned++; continue; }

            // The process is live and is one of ours. Delete its record only on
            // a PROVEN mismatch (both timestamps readable, and different — a
            // recycled pid). A record we merely failed to understand is kept:
            // throwing it away converts "provably idle" into "unknown", which
            // is how a parsing bug quietly disarmed the whole feature once.
            var claimed = ParseIso(rec["startedUtc"]?.ToString());
            if (claimed is null || started is null) continue;
            if (Math.Abs((claimed.Value - started.Value).TotalSeconds) <= 2) continue;
            if (TryDeleteRecord(pid)) pruned++;
        }
        return pruned;
    }

    /// <summary>
    /// JObject.Parse with the date magic turned OFF.
    ///
    /// Newtonsoft's default DateParseHandling rewrites any string that looks
    /// like ISO 8601 into a DateTime token, and JValue.ToString() on a date
    /// token renders it in the CURRENT CULTURE — so "2026-08-23T15:24:40Z"
    /// came back out as "23/08/2026 22:24:40", the invariant ISO parse failed,
    /// and every heartbeat on the machine read as "no heartbeat". The reaper
    /// then never reaped anything and, worse, pruned the very records it had
    /// just failed to understand. We wrote these strings; we want them back
    /// exactly as written.
    /// </summary>
    private static JObject ParseVerbatim(string json)
    {
        using var sr = new StringReader(json);
        using var jr = new Newtonsoft.Json.JsonTextReader(sr)
        {
            DateParseHandling = Newtonsoft.Json.DateParseHandling.None,
        };
        return JObject.Load(jr);
    }

    private static bool TryDeleteRecord(int pid)
    {
        try { File.Delete(Path.Combine(InstancesDir, pid + ".json")); return true; }
        catch { return false; }
    }

    /// <summary>Process-level twin of <see cref="ParentGone"/>, for judging
    /// somebody else's parent from their record rather than our own from
    /// memory. Same rule: only "gone" when we can prove it.</summary>
    private static bool IsGone(int pid, DateTime? claimedStart)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return true;
            var started = StartTimeUtc(p);
            if (claimedStart is { } want && started is { } got)
                return Math.Abs((want - got).TotalSeconds) > 2;
            return false;
        }
        catch (ArgumentException) { return true; }
        catch { return false; }
    }

    /// <summary>pid + start time. Windows reissues pids freely, and "the record
    /// says pid 22456 is idle" must never become "kill whatever holds 22456
    /// now".</summary>
    private static bool LooksLikeSameProcess(JObject rec, DateTime? actualStart)
    {
        var claimed = ParseIso(rec["startedUtc"]?.ToString());
        if (claimed is null || actualStart is null) return false;
        return Math.Abs((claimed.Value - actualStart.Value).TotalSeconds) <= 2;
    }

    // ── OS helpers ───────────────────────────────────────────────────

    private static List<Process> SafeGetProcesses()
    {
        try { return Process.GetProcessesByName("brainx-mcp").ToList(); }
        catch { return new List<Process>(); }
    }

    private static DateTime? StartTimeUtc(Process p)
    {
        try { return p.StartTime.ToUniversalTime(); } catch { return null; }
    }

    private static string Iso(DateTime utc) =>
        utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTime? ParseIso(string? s) =>
        DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d : null;

    internal static string Human(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{t.TotalDays:0.#}d"
        : t.TotalHours >= 1 ? $"{t.TotalHours:0.#}h"
        : $"{t.TotalMinutes:0}m";

    private static int ParentPid(int pid) => ParentMap().TryGetValue(pid, out var p) ? p : 0;

    // Toolhelp32 rather than System.Management: the MCP ships to macOS and
    // Linux too, and dragging WMI in for a parent lookup would cost every
    // platform for one platform's benefit. Cached per sweep — one snapshot of
    // a few hundred processes is cheap, a snapshot per process is not.
    private static Dictionary<int, int>? _parentCache;
    private static DateTime _parentCacheAt = DateTime.MinValue;

    private static Dictionary<int, int> ParentMap()
    {
        if (_parentCache != null && DateTime.UtcNow - _parentCacheAt < TimeSpan.FromSeconds(5))
            return _parentCache;

        var map = new Dictionary<int, int>();
        if (OperatingSystem.IsWindows())
        {
            var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap != IntPtr.Zero && snap != new IntPtr(-1))
            {
                try
                {
                    var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
                    if (Process32First(snap, ref entry))
                        do { map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID; }
                        while (Process32Next(snap, ref entry));
                }
                finally { CloseHandle(snap); }
            }
        }
        else
        {
            // /proc/<pid>/stat field 4 is ppid. Linux only; on macOS the map
            // stays empty, which downgrades "is this a worker" to unknown and
            // makes the sweep more conservative, never less.
            try
            {
                foreach (var dir in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
                    try
                    {
                        var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                        var tail = stat[(stat.LastIndexOf(')') + 1)..].Trim().Split(' ');
                        if (tail.Length > 1 && int.TryParse(tail[1], out var ppid)) map[pid] = ppid;
                    }
                    catch { }
                }
            }
            catch { }
        }

        _parentCache = map;
        _parentCacheAt = DateTime.UtcNow;
        return map;
    }

    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
