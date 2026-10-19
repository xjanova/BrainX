using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BrainX.Core.Services;
using BrainX.Core.Services.Chrome;

namespace BrainX.Mcp.Bridge;

/// <summary>
/// The built-in Claude in Chrome bridge: <c>claude.exe --claude-in-chrome-mcp</c>
/// behind the hub, with its tools as <c>chrome__*</c>.
///
/// What makes it different from a unity/unreal bridge, all of it deliberate:
///
///  • OPT-IN. Off until the owner ticks it in BrainX ▸ Settings (the file
///    lives in the private folder, see ChromeBridgeSettings). Re-read every few
///    seconds, so switching it off takes effect on the next call, not the next
///    session.
///  • NO CONFIG ENTRY. The exe is re-resolved before every spawn
///    (ClaudeCodeLocator) because the desktop app deletes old versions on update.
///  • PRIVATE. Arguments and results never touch the vault: no journal, no
///    activity line, no agent-bus counter, no status file, no schema cache in
///    the vault. They go to the private store only (PrivateBrowseStore).
///  • NARROWER. file_upload is never listed nor forwarded; the child gets an
///    environment without the parent Claude session's variables.
///  • LAZY AND BOUNDED. An idle claude.exe costs ~150 MB per brainx-mcp
///    session, so discovery closes it again, a call opens it on demand, and
///    fifteen idle minutes close it.
///  • LOCAL ONLY. The hub is already off in the headless remote child; on top
///    of that the remote policy hard-blocks chrome__* and private_browse_search.
/// </summary>
internal static class ChromeBridge
{
    public static readonly TimeSpan IdleClose = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SettingsTtl = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AuthTtl = TimeSpan.FromSeconds(60);

    private static readonly object Gate = new();
    private static Action<string> _log = _ => { };
    private static Func<string> _identity = () => "agent";
    private static string _vaultPath = "";
    private static bool _initialized;

    private static ChromeBridgeSettings _settings = new();
    private static DateTime _settingsReadUtc = DateTime.MinValue;

    private static int _connecting;   // spawns in progress (discovery and a call can overlap)
    private static string? _lastOutcome;
    private static DateTime? _lastOutcomeUtc;
    private static string? _failureDetail;
    private static string? _serverName, _serverVersion;
    private static DateTime _lastCallUtc = DateTime.MinValue;

    private static (ClaudeAuthResult Result, string Exe, DateTime At)? _auth;

    /// <summary>The one definition. Command/args here are placeholders — every
    /// spawn gets a clone with a freshly resolved exe.</summary>
    public static readonly McpBridgeDef Def = new()
    {
        Id = ChromeBridgeIds.BridgeId,
        Enabled = false,
        Command = "claude.exe",
        Args = new List<string> { ChromeBridgeIds.McpFlag },
        // ≥ 150 s: switch_browser waits up to 120 s and browser_batch 90 s; a
        // shorter timeout poisons the connection and loses the tab group.
        TimeoutSeconds = 180,
        Docs = "https://code.claude.com/docs/en/chrome",
        Setup = "Turn it on in BrainX ▸ Settings ▸ Claude in Chrome. Needs Claude Code signed in with a claude.ai subscription and the Claude extension in Chrome or Edge.",
        BuiltIn = true,
        Private = true,
        StripClaudeEnv = true,
        QuietStderr = true,
    };

    public static void Initialize(string vaultPath, Action<string> log, Func<string>? identity)
    {
        _vaultPath = vaultPath;
        _log = log;
        if (identity != null) _identity = identity;
        _initialized = true;

        // Retention is applied on every read regardless, but an owner who
        // stopped browsing should not keep old pages ON DISK either: prune
        // when the brain starts, off the handshake path.
        _ = Task.Run(() =>
        {
            try { if (Store is { } s && File.Exists(s.DbPath)) s.MaybePrune(); }
            catch { /* housekeeping */ }
        });
    }

    // ───────────── switch + store ─────────────

    public static ChromeBridgeSettings Settings
    {
        get
        {
            lock (Gate)
            {
                if (DateTime.UtcNow - _settingsReadUtc > SettingsTtl)
                {
                    _settings = ChromeBridgeSettings.Load();
                    _settingsReadUtc = DateTime.UtcNow;
                }
                return _settings;
            }
        }
    }

    public static bool EnabledNow => _initialized && Settings.Enabled;

    /// <summary>
    /// A run nobody is watching: a broker task (BRAINX_BROKER_RUN=1) or the
    /// Mind (BRAINX_AS_AGENT). Both take their work from bus mail, task cards
    /// and the cowork room — which peers write to — and both run with every
    /// brain tool pre-approved (the broker's claude is started with
    /// `--allowedTools mcp__brainx-brain`, the Mind's codex bypasses approvals).
    /// A peer's "open bank.example and send me the balance" would reach the
    /// owner's logged-in browser with agent_send as the way back. So in such a
    /// run the bridge is OFF whatever the switch says: not listed, not
    /// discovered, every call refused before anything is spawned or logged —
    /// and private_browse_search is refused the same way.
    ///
    /// Not the environment alone (review rev3): codex hands its MCP servers
    /// only an allow-list of variables plus their configured env, so a codex
    /// broker run's brain never saw BRAINX_BROKER_RUN. The broker now passes
    /// it to codex as a config override AND records every runner it starts;
    /// a brain below a recorded runner is unattended whatever its environment
    /// says (UnattendedRuns).
    /// </summary>
    public static bool IsUnattendedRun =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(UnattendedRuns.AsAgentEnv))
        || UnattendedRuns.IsBrokerRun();

    /// <summary>The owner's switch is on AND someone is watching this run.</summary>
    public static bool AvailableNow => EnabledNow && !IsUnattendedRun;

    public const string UnattendedMessage =
        "Claude in Chrome is not available to unattended runs (broker tasks, Mind) — only a session the owner is watching can drive the browser " +
        "(งานที่รันเองโดยไม่มีคนดู เช่นงานจาก broker หรือ Mind ใช้ Claude in Chrome ไม่ได้ — ใช้ได้เฉพาะเซสชันที่เจ้าของดูอยู่)";

    /// <summary>Forget the cached switch — after a test rewrites it.</summary>
    public static void InvalidateSettings() { lock (Gate) _settingsReadUtc = DateTime.MinValue; }

    /// <summary>
    /// The private store, or null when it would land INSIDE the vault (a
    /// misdirected BRAINX_PRIVATE_DIR). Better not to keep a log than to keep
    /// it where every sync path carries it.
    /// </summary>
    public static PrivateBrowseStore? Store
    {
        get
        {
            var root = Path.GetFullPath(ChromeBridgeSettings.PrivateRoot);
            if (!string.IsNullOrEmpty(_vaultPath) && IsUnder(root, _vaultPath)) return null;
            return new PrivateBrowseStore(root);
        }
    }

    private static bool IsUnder(string path, string root)
    {
        try
        {
            var p = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }   // unknowable → assume the worst
    }

    public static bool IsHidden(string remoteTool) => ChromeBridgeIds.NeverExposed.Contains(remoteTool);

    public static string DisabledMessage =>
        "the Claude in Chrome bridge is off — the owner turns it on in BrainX ▸ Settings ▸ Claude in Chrome " +
        "(การเชื่อม Claude in Chrome ปิดอยู่ — เปิดได้ที่ BrainX ▸ ตั้งค่า ▸ Claude in Chrome)";

    public static string HiddenMessage(string tool) =>
        $"'{tool}' is not available through BrainX: it reads local files by path, which a browser tool must not do " +
        $"('{tool}' ใช้ผ่าน BrainX ไม่ได้ — เครื่องมือนี้อ่านไฟล์ในเครื่องตาม path)";

    public const string NoSuchToolMessage =
        "no such Claude in Chrome tool — call tools/list for the chrome__ names " +
        "(ไม่มีเครื่องมือ Claude in Chrome ชื่อนี้ — ดูชื่อ chrome__ ได้จาก tools/list)";

    /// <summary>
    /// What a failed chrome__ call tells the agent (and, through CluadeX, the
    /// owner): English and Thai, never the raw transport text alone. A message
    /// that already carries Thai (a spawn refusal) passes through.
    /// </summary>
    public static string ErrorText(Exception ex)
    {
        var msg = ex.Message;
        if (msg.Any(c => c >= '฀' && c <= '๿')) return "chrome bridge: " + msg;

        var secs = System.Text.RegularExpressions.Regex.Match(msg, @"(\d+(?:\.\d+)?)\s?s\b");
        var n = secs.Success ? secs.Groups[1].Value : "?";
        string th;
        if (msg.Contains("closed the pipe", StringComparison.OrdinalIgnoreCase))
            th = $"เซิร์ฟเวอร์ Claude in Chrome ปิดการเชื่อมต่อหลังจาก {n} วินาที — การเรียกนี้อาจทำงานไปแล้ว ตรวจหน้าเว็บก่อนลองใหม่";
        else if (msg.Contains("busy with another request", StringComparison.OrdinalIgnoreCase))
            th = $"เซิร์ฟเวอร์ Claude in Chrome กำลังทำคำขออื่นอยู่นาน {n} วินาที — ลองใหม่อีกครั้ง";
        else if (ex is TimeoutException || msg.Contains("no response within", StringComparison.OrdinalIgnoreCase))
            th = $"Claude in Chrome ไม่ตอบภายใน {n} วินาที — การเรียกนี้อาจทำงานไปแล้ว ตรวจหน้าเว็บก่อนลองใหม่";
        else if (msg.Contains("server exited", StringComparison.OrdinalIgnoreCase))
            th = "เซิร์ฟเวอร์ Claude in Chrome ปิดตัวไปแล้ว — จะเริ่มใหม่ในการเรียกครั้งถัดไป";
        else
            th = "การเรียก Claude in Chrome ไม่สำเร็จ";
        return $"chrome bridge: {msg} ({th})";
    }

    // ───────────── sandbox (verification harness only) ─────────────

    private static bool Sandbox => Environment.GetEnvironmentVariable("BRAINX_SANDBOX") == "1";

    /// <summary>
    /// In a sandbox the bridge NEVER looks for the owner's real claude.exe — it
    /// runs only the stub the harness names, or nothing. A test must not be
    /// able to drive the owner's browser by accident.
    /// </summary>
    private static string? SandboxStub()
    {
        if (!Sandbox) return null;
        var stub = Environment.GetEnvironmentVariable("BRAINX_CHROME_STUB_COMMAND");
        return !string.IsNullOrWhiteSpace(stub) && Path.IsPathRooted(stub) && File.Exists(stub) ? stub : null;
    }

    // ───────────── discovery of claude.exe ─────────────

    public static ClaudeLocateResult Locate()
    {
        if (Sandbox)
        {
            var stub = SandboxStub();
            if (stub == null) return new ClaudeLocateResult { Status = ClaudeLocateStatus.NotFound };
            return new ClaudeLocateResult
            {
                Status = ClaudeLocateStatus.Found,
                Install = new ClaudeCodeInstall(stub, ClaudeInstallKind.Override, stub, Array.Empty<string>(), "2.1.293"),
            };
        }
        return ClaudeCodeLocator.Locate(new ClaudeCodeLocator.Options { Override = Settings.ClaudePathOverride });
    }

    private static ClaudeAuthResult? Auth(ClaudeCodeInstall? install)
    {
        if (install == null) return null;
        if (Sandbox)
        {
            // The stub answers with one of Claude Code's real authMethod
            // values, run through the same classifier as a live answer.
            var method = (Environment.GetEnvironmentVariable("BRAINX_CHROME_STUB_AUTH") ?? "claude.ai") switch
            {
                "signed-in" => "claude.ai",
                "apikey" => "api_key",
                var m => m,
            };
            return new ClaudeAuthResult(ClaudeAuthProbe.Classify(method), method, "cli");
        }
        lock (Gate)
            if (_auth is { } a && a.Exe == install.Path && DateTime.UtcNow - a.At < AuthTtl) return a.Result;
        var r = ClaudeAuthProbe.Probe(install);
        lock (Gate) _auth = (r, install.Path, DateTime.UtcNow);
        return r;
    }

    private static ChromeScanResult Scan()
    {
        if (Sandbox)
        {
            var root = Environment.GetEnvironmentVariable("BRAINX_CHROME_STUB_PROFILES");
            var none = Path.Combine(Path.GetTempPath(), "brainx-no-profiles-" + Environment.ProcessId);
            var running = Environment.GetEnvironmentVariable("BRAINX_CHROME_STUB_RUNNING") == "1";
            return ChromeExtensionScanner.Scan(new ChromeExtensionScanner.Options
            {
                LocalAppData = string.IsNullOrWhiteSpace(root) ? none : root,
                AppData = string.IsNullOrWhiteSpace(root) ? none : Path.Combine(root, "Roaming"),
                IsRunning = _ => running,
            });
        }
        return ChromeExtensionScanner.Scan();
    }

    // ───────────── spawning ─────────────

    /// <summary>
    /// Start the server. Re-resolves the exe every time; refuses (with the
    /// reason as the message) when there is nothing usable, rather than start
    /// something that is not the chrome server.
    /// </summary>
    public static IMcpBridgeConnection StartConnection(Action<string> log)
    {
        lock (Gate) _connecting++;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var spawn = ResolveSpawn();
                try
                {
                    var conn = McpBridgeConnection.Start(spawn, log);
                    lock (Gate)
                    {
                        _serverName = conn.ServerName;
                        _serverVersion = conn.ServerVersion;
                        _failureDetail = null;
                        if (_lastOutcome == "failed") _lastOutcome = null;
                    }
                    return conn;
                }
                catch (FileNotFoundException) when (attempt == 0)
                {
                    // The desktop app updated under us and deleted the folder
                    // the exe was resolved from a moment ago. Resolve again.
                    ClaudeCodeLocator.ClearCache();
                }
            }
        }
        catch (Exception ex)
        {
            SetFailed(ex.Message);
            throw;
        }
        finally
        {
            lock (Gate) _connecting--;
        }
    }

    private static McpBridgeDef ResolveSpawn()
    {
        var loc = Locate();
        if (loc.Status != ClaudeLocateStatus.Found || loc.Install == null)
            throw new InvalidOperationException(loc.Status switch
            {
                ClaudeLocateStatus.TooOld =>
                    $"Claude Code {loc.RejectedVersion} at {loc.Rejected?.Path} is too old for Claude in Chrome (needs 2.1+) — update it " +
                    $"(Claude Code {loc.RejectedVersion} เก่าเกินไปสำหรับ Claude in Chrome ต้องเป็น 2.1 ขึ้นไป — อัปเดตก่อน)",
                ClaudeLocateStatus.Broken =>
                    $"Claude Code at {loc.Rejected?.Path} did not answer --version " +
                    "(Claude Code ไม่ตอบคำสั่ง --version — ลองติดตั้งใหม่)",
                _ => Sandbox
                    ? "sandbox: no chrome stub configured (BRAINX_CHROME_STUB_COMMAND) — the real claude.exe is never started in a sandbox " +
                      "(แซนด์บ็อกซ์: ไม่ได้ตั้ง stub จึงไม่เริ่ม claude.exe จริง)"
                    : "Claude Code isn't installed on this PC — see https://code.claude.com/docs/en/setup " +
                      "(ยังไม่พบ Claude Code ในเครื่องนี้ — ดูวิธีติดตั้งที่ https://code.claude.com/docs/en/setup)",
            });

        var install = loc.Install;
        var spawn = Def.CloneForSpawn();
        spawn.Command = install.Command;
        spawn.Args = install.McpArgs.ToList();

        // The flag must be the FIRST argument the CLI sees (args[1] under
        // node). Anything else would start the interactive CLI on our pipe.
        if (spawn.Args.Count <= install.FlagIndex || spawn.Args[install.FlagIndex] != ChromeBridgeIds.McpFlag)
            throw new InvalidOperationException("refusing to start Claude Code: --claude-in-chrome-mcp is not the first argument " +
                                                "(ไม่เริ่ม Claude Code เพราะ --claude-in-chrome-mcp ไม่ใช่อาร์กิวเมนต์แรก)");
        return spawn;
    }

    private static void SetFailed(string message)
    {
        // The status carries its own en + th messages around this detail, so
        // keep the English part of a bilingual refusal.
        var cut = message.IndexOf(" (", StringComparison.Ordinal);
        if (cut > 0 && message[cut..].Any(c => c >= '฀' && c <= '๿')) message = message[..cut];
        lock (Gate)
        {
            _lastOutcome = "failed";
            _lastOutcomeUtc = DateTime.UtcNow;
            _failureDetail = message.Length > 300 ? message[..300] + "…" : message;
        }
    }

    // ───────────── after a call ─────────────

    public static void NoteCallStart() { lock (Gate) _lastCallUtc = DateTime.UtcNow; }

    public static bool IdleExpired()
    {
        lock (Gate) return _lastCallUtc != DateTime.MinValue && DateTime.UtcNow - _lastCallUtc > IdleClose;
    }

    // ───────────── the private log writer ─────────────
    //
    // Keeping a call costs SecretRedactor over the page, an FTS5 trigram insert
    // and DPAPI per screenshot — measured at 0.7 s median and 2.3 s worst for
    // ~100 KB pages. Done inline, that landed before the agent got its result
    // and, brainx-mcp being one request at a time, before every other brain
    // tool too. So the call only CAPTURES (strings and decoded bytes, before
    // the 256 KB cap rewrites the result, clipped to what the store keeps —
    // PrivateBrowseCapture) and a single background writer stores, in order.
    // Bounded in entries (64) AND in bytes (64 MB, PrivateBrowseQueue): past
    // either bound the OLDEST pending entry is dropped (and counted, never
    // logged with content) rather than memory growing behind a slow disk.
    // Flushed before a search and at shutdown.

    private static readonly PrivateBrowseQueue Pending = new();

    private static long _queued;      // enqueued and not yet stored (or dropped)
    private static Task? _writer;
    private static readonly object WriterGate = new();

    /// <summary>Entries dropped because the writer fell behind (no content, a count).</summary>
    public static long DroppedRecords => Pending.Dropped;

    private static void Enqueue(PrivateBrowseRecord rec)
    {
        lock (WriterGate) _writer ??= Task.Run(WriteLoopAsync);
        Interlocked.Increment(ref _queued);
        var dropped = Pending.Enqueue(rec);
        if (dropped > 0) Interlocked.Add(ref _queued, -dropped);
    }

    private static async Task WriteLoopAsync()
    {
        while (true)
        {
            await Pending.WaitAsync().ConfigureAwait(false);
            while (Pending.TryDequeue(out var rec))
            {
                try
                {
                    // Harness only: a slow disk, on demand, so "the call does
                    // not wait for the log" can be proven rather than hoped.
                    if (Sandbox && int.TryParse(Environment.GetEnvironmentVariable("BRAINX_PRIVATE_LOG_DELAY_MS"), out var delay) && delay > 0)
                        await Task.Delay(Math.Min(delay, 30_000)).ConfigureAwait(false);
                    var store = Store;
                    store?.Record(rec, Settings.RetentionDays);
                }
                catch (Exception ex)
                {
                    // The type only: the message of a failed write could quote content.
                    _log($"chrome: private log write failed ({ex.GetType().Name}) — the call itself is unaffected");
                }
                finally { Interlocked.Decrement(ref _queued); }
            }
        }
    }

    /// <summary>
    /// Wait until every captured call is stored (or <paramref name="timeout"/>
    /// passes). True when nothing is pending. Before a private search — the
    /// owner should find the page that was just read — and at shutdown.
    /// </summary>
    public static bool FlushPrivateLog(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Interlocked.Read(ref _queued) > 0)
        {
            if (DateTime.UtcNow >= until) return false;
            Thread.Sleep(25);
        }
        return true;
    }

    /// <summary>
    /// Learn the bridge state from a result and hand the call to the private
    /// log writer. Never throws, never logs content. Runs BEFORE the result is
    /// capped for the agent, so the log keeps what the server actually said.
    /// </summary>
    public static void AfterCall(string remoteTool, JObject args, JObject? result, Exception? error)
    {
        try { UpdateOutcome(remoteTool, result, error); } catch { }

        try
        {
            if (Store == null) return;
            // Clipped at capture to what the store keeps (text, args, URL, 8
            // shots of ≤ 15 MB) — what is stored does not change — and the
            // queue holds at most 64 entries and 64 MB (or one entry).
            Enqueue(PrivateBrowseCapture.FromCall(remoteTool, args, result, error, SafeIdentity()));
        }
        catch (Exception ex)
        {
            // The type only: the message of a failed capture could quote content.
            _log($"chrome: private log capture failed ({ex.GetType().Name}) — the call itself is unaffected");
        }
    }

    private static void UpdateOutcome(string remoteTool, JObject? result, Exception? error)
    {
        if (error != null)
        {
            // A tool-level error from a healthy server is still a connection
            // that works; only a transport failure counts as "failed". So is
            // a request that merely waited for the pipe (nothing broke).
            if (error.Message.Contains("busy with another request", StringComparison.OrdinalIgnoreCase)) return;
            if (error is TimeoutException or IOException
                || error.Message.Contains("server exited", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("closed the pipe", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("poisoned", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("write failed", StringComparison.OrdinalIgnoreCase))
                SetFailed(error.Message);
            return;
        }

        var text = string.Concat((result?["content"] as JArray ?? new JArray()).OfType<JObject>()
            .Where(b => b["type"]?.ToString() == "text").Select(b => b["text"]?.ToString() + "\n"));
        string outcome;
        if (text.TrimStart().StartsWith("Browser extension is not connected", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
            outcome = "not-reachable";
        else if (text.Contains("browsers are connected to this account", StringComparison.OrdinalIgnoreCase)
                 && text.Contains("none has been selected", StringComparison.OrdinalIgnoreCase))
            outcome = "multi-browser";
        else if (remoteTool == "list_connected_browsers" && RosterNeedsChoice(text))
            outcome = "multi-browser";
        else
            outcome = "ok";

        lock (Gate)
        {
            _lastOutcome = outcome;
            _lastOutcomeUtc = DateTime.UtcNow;
            if (outcome == "ok") _failureDetail = null;
        }
    }

    /// <summary>More than one browser connected and none marked inUse.</summary>
    private static bool RosterNeedsChoice(string text)
    {
        var s = text.IndexOf('[');
        var e = text.LastIndexOf(']');
        if (s < 0 || e <= s) return false;
        try
        {
            var arr = JArray.Parse(text[s..(e + 1)]);
            return arr.Count > 1 && !arr.OfType<JObject>().Any(b => b["inUse"]?.Type == JTokenType.Boolean && b["inUse"]!.Value<bool>());
        }
        catch { return false; }
    }

    private static string SafeIdentity()
    {
        try { return _identity(); } catch { return "agent"; }
    }

    // ───────────── bridge_status ─────────────

    /// <summary>
    /// The "chrome" entry. With the bridge off or remote, nothing is probed or
    /// spawned — the owner has not asked for Claude Code to be touched.
    /// </summary>
    public static JObject StatusEntry(bool hubEnabled, bool live, int tools, string? clientExe = null)
    {
        var remote = !hubEnabled;
        // Unattended: the bridge is off for this run whatever the switch says,
        // so nothing is probed and the private log is not offered either.
        var unattended = !remote && IsUnattendedRun;
        var settings = _initialized ? Settings : new ChromeBridgeSettings();
        var enabled = !remote && !unattended && settings.Enabled;
        var store = remote || unattended ? null : Store;

        ClaudeLocateResult? loc = null;
        ClaudeAuthResult? auth = null;
        ChromeScanResult? scan = null;
        if (enabled)
        {
            try { loc = Locate(); } catch { }
            try { if (loc?.Status == ClaudeLocateStatus.Found) auth = Auth(loc.Install); } catch { }
            try { scan = Scan(); } catch { }
        }

        ChromeRuntimeSnapshot rt;
        lock (Gate)
        {
            rt = new ChromeRuntimeSnapshot
            {
                Live = live,
                Connecting = _connecting > 0,
                LastOutcome = _lastOutcome,
                LastOutcomeUtc = _lastOutcomeUtc,
                FailureDetail = _failureDetail,
                ServerName = _serverName,
                ServerVersion = _serverVersion,
                Tools = tools,
            };
        }

        return ChromeBridgeStatus.Build(new ChromeBridgeStatus.Inputs
        {
            Enabled = enabled,
            Remote = remote,
            Unattended = unattended,
            Locate = loc,
            Auth = auth,
            Scan = scan,
            Runtime = rt,
            RetentionDays = settings.RetentionDays,
            PrivateLogPath = store?.Root,
            PrivateLogAvailable = store != null,
            BrainXClientExe = clientExe,
        });
    }

    // ───────────── schema cache (private folder, never the vault) ─────────────

    public static string CachePath => Path.Combine(ChromeBridgeSettings.PrivateRoot, "chrome-tools.cache.json");

    public static JObject? ReadCacheEntry()
    {
        try
        {
            var p = CachePath;
            return File.Exists(p) ? JObject.Parse(File.ReadAllText(p)) : null;
        }
        catch { return null; }
    }

    public static void WriteCacheEntry(JObject entry)
    {
        try
        {
            var root = ChromeBridgeSettings.PrivateRoot;
            if (!string.IsNullOrEmpty(_vaultPath) && IsUnder(root, _vaultPath)) return;
            PrivateBrowseStore.EnsurePrivateDirectory(root);
            var path = CachePath;
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, entry.ToString(Formatting.Indented), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { _log($"chrome: tool cache write failed ({ex.GetType().Name})"); }
    }
}
