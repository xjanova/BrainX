using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

// ─────────────────────────────────────────────────────────────────────────
// The cowork room.
//
// Owner (2026-09-19): "ให้มีพื้นที่ที่ฉันรู้ว่าพวกคุณสื่อสารอะไรกัน ... เหมือนเป็น
// cowork space ไม่ว่าจะเชื่อมต่อกับใครก็คุยกันในนั้น เหมือนห้องทำงานไง ... ฉันเข้าไป
// พิมพ์ ทุกคนต้องฟัง ตรงนั้น เหมือนบอสเข้าไปสั่งการ เรียกงานดู ... ส่งรูป ส่งไฟล์
// ฉันก็เห็น".
//
// Everything the room draws already existed on disk; nothing was showing it in
// one place. presence/ says who is at a desk, the calls counter says whether
// they are typing, inbox/ and read/ are the conversation, broker/decisions/ is
// what they have stopped on, and files/ holds what they sent. This file is the
// window onto that, and the one place the owner can speak INTO it.
//
// The owner's line is written HERE, not by the page. An identity the agents
// trust has to come from a process the owner controls: brainx-mcp reserves
// "owner" so no handshake can claim it, and the document in the WebView can
// only ask this code to send — it cannot address the bus itself.
// ─────────────────────────────────────────────────────────────────────────

public partial class MainWindow
{
    private DispatcherTimer? _coworkTimer;
    private bool _coworkWired;

    /// <summary>Two seconds, the same cadence the dashboard cards poll at. The
    /// room is watched, not stared at, and every tick reads a directory.</summary>
    private static readonly TimeSpan CoworkPoll = TimeSpan.FromSeconds(2);

    /// <summary>How much conversation the room keeps. Matches the panel, which
    /// scrolls — sending more would be a payload nobody reads.</summary>
    private const int CoworkKeep = 80;

    private string CoworkBusRoot => Path.Combine(_vaultPath, ".obsidianx", "agent-bus");

    private async Task InitializeCoworkAsync()
    {
        // Popped out: the room is live in its own window, and the dashboard's
        // view only says where it went (MainWindow.CoworkPopout).
        if (CoworkPoppedOut)
        {
            ShowCoworkPoppedOut(true);
            EnsureCoworkTimer();
            return;
        }
        try
        {
            await CoworkWebView.EnsureCoreWebView2Async(await GetAppWebViewEnvAsync()).ConfigureAwait(true);
            var core = CoworkWebView.CoreWebView2;
            if (core == null) return;

            if (!_coworkWired)
            {
                var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
                if (!Directory.Exists(wwwroot)) return;
                core.SetVirtualHostNameToFolderMapping(
                    "universe.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

                // The bus gets its own host so a picture an agent sent can be
                // shown with a plain <img src>. Mapped read-only and rooted at
                // the bus, not the vault: the room has no business serving the
                // owner's notes to a document.
                if (Directory.Exists(CoworkBusRoot))
                    core.SetVirtualHostNameToFolderMapping(
                        "bus.local", CoworkBusRoot, CoreWebView2HostResourceAccessKind.DenyCors);

                core.WebMessageReceived += OnCoworkMessage;
                await MapMindAvatarAsync(core);
                // Cache-bust on the room's own files.
                //
                // WebView2 caches what a virtual host serves exactly like any
                // other origin, so a rebuilt office.js keeps serving the old
                // one until the profile is cleared — which looked, from the
                // outside, like a build that had not been deployed. The stamp
                // is the newest write time under office/, so the URL changes
                // when and only when the room actually changed.
                core.Navigate("https://universe.local/office/index.html?v=" + CoworkAssetStamp(wwwroot));
                _coworkWired = true;
            }

            EnsureCoworkTimer();
            PostCowork();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cowork init: {ex.Message}"); }
    }

    private void EnsureCoworkTimer()
    {
        _coworkTimer ??= new DispatcherTimer { Interval = CoworkPoll };
        if (_coworkTimer.Tag is not bool)
        {
            _coworkTimer.Tick += (_, _) => PostCowork();
            _coworkTimer.Tag = true;
        }
        _coworkTimer.Start();
    }

    /// <summary>The room is only worth reading while it is on screen — and a
    /// room in its own window is on screen whatever the dashboard shows.</summary>
    private void StopCowork()
    {
        if (CoworkPoppedOut) return;
        _coworkTimer?.Stop();
    }

    /// <summary>Newest write time under office/, as a compact stamp. Cheap:
    /// a dozen files, read once per session when the room is first opened.</summary>
    private static string CoworkAssetStamp(string wwwroot)
    {
        try
        {
            var dir = Path.Combine(wwwroot, "office");
            if (!Directory.Exists(dir)) return "0";
            var newest = new DirectoryInfo(dir)
                .EnumerateFiles("*.*", SearchOption.AllDirectories)
                .Max(f => (DateTime?)f.LastWriteTimeUtc) ?? DateTime.UnixEpoch;
            return newest.Ticks.ToString();
        }
        catch { return DateTime.UtcNow.Ticks.ToString(); }
    }

    // ───────────── what the room is told ─────────────

    private void PostCowork()
    {
        try
        {
            var agents = CoworkAgents();
            var payload = new JObject
            {
                ["busUrl"] = "https://bus.local/",
                // The boss of the room, as a fact the room can draw: it is the
                // broker that reads what was said here and starts whoever
                // should be working. Owner (2026-09-20): "บอสก็คือ โบรกเกอร์
                // จัดสรร คอยจี้นั่นแหละ".
                ["broker"] = CoworkBrokerState(),
                ["agents"] = agents,
                // What each agent has left of its usage limits — its HP.
                // มาย has no presence file — she stands in the room as the
                // secretary — so she is added by hand while she is on.
                ["quota"] = CoworkQuota(agents.OfType<JObject>()
                    .Where(a => a["bridge"]?.ToObject<bool?>() != true)
                    .Select(a => a["id"]?.ToString() ?? "").Where(s => s.Length > 0)
                    .Concat(SecretaryOn ? new[] { "mind" } : Array.Empty<string>())),
                ["messages"] = CoworkMessages(),
                ["decisions"] = CoworkDecisions(),
                // Who is doing what. Built from the task files the agents
                // write with cowork_task, never parsed out of the chat.
                ["tasks"] = CoworkTasks(),
                ["roomOpen"] = CoworkRoomIsOpen(),
                // Which model the boss starts each agent on, and what it can be.
                ["models"] = CoworkModels(),
                // The runs the boss started that are still going — what the
                // work window can stop.
                ["runs"] = CoworkRuns(),
                // Mind as the room's secretary: on/off, what she may do, the
                // models on this machine (MainWindow.CoworkSecretary).
                ["secretary"] = SecretaryPayload(),
            };
            // To wherever the room is showing: its own window when popped out,
            // the dashboard's view otherwise — never both.
            CoworkSurface?.PostWebMessageAsJson(
                new JObject { ["type"] = "officeState", ["payload"] = payload }.ToString());
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"PostCowork: {ex.Message}"); }
    }

    /// <summary>
    /// Who has a desk, and what they are doing at it.
    ///
    /// The three states come from two facts the bus already writes: a fresh
    /// heartbeat means the MCP process is alive, and a MOVING call counter
    /// means the model is actually doing something. A parked session and a
    /// busy one both report "online" — the counter is the only thing that
    /// separates them, which is the same reading the broker makes.
    /// </summary>
    private JArray CoworkAgents()
    {
        var arr = new JArray();
        var dir = Path.Combine(CoworkBusRoot, "presence");
        if (!Directory.Exists(dir)) return arr;

        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            JObject o;
            try { o = JObject.Parse(File.ReadAllText(f)); } catch { continue; }

            var id = Path.GetFileNameWithoutExtension(f);
            // Probe identities that connected once months ago are not staff.
            // Everything else earns a desk, including agents that are offline:
            // an empty chair is information.
            var seen = DateTime.TryParse(o["lastSeenUtc"]?.ToString(), null,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
                ? (DateTime?)t : null;
            if (seen is null || (DateTime.UtcNow - seen.Value).TotalDays > 14) continue;

            var age = (DateTime.UtcNow - seen.Value).TotalSeconds;
            var calls = o["calls"]?.ToObject<long?>() ?? 0;
            var online = age <= 90;

            // Per session when the sessions publish themselves: every window of
            // one agent overwrites this file with its own counter, so with two
            // windows open the number changed every heartbeat and the desk read
            // "working" while both sat at a prompt.
            var sig = CoworkCallSignature(id) ?? calls.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var prev = _coworkCalls.TryGetValue(id, out var c) ? c : null;
            _coworkCalls[id] = sig;
            var moving = prev != null && sig != prev;

            arr.Add(new JObject
            {
                ["id"] = id,
                ["label"] = o["client"]?.ToString() is { Length: > 0 } cl && cl != "unknown" ? id : id,
                ["state"] = !online ? "offline" : (moving || age < 8 ? "working" : "idle"),
                ["lastTool"] = o["lastTool"]?.ToString() ?? "",
                ["pending"] = CoworkPending(id),
                ["spawned"] = CoworkWasSpawned(id),
                // Sitting at a desk and LISTENING are two different facts now.
                // Presence says the process is alive; the room's member file
                // says this session joined and will be told what is said here.
                // An agent at a desk with no headset is working on something
                // else, and the owner should be able to see that before they
                // type an order nobody is going to hear.
                ["inRoom"] = CoworkInRoom(id),
                // Who they chose to look like, and how it is going right now.
                // Both read from disk every tick rather than cached: an agent
                // changing its own face mid-session is exactly the moment the
                // owner is watching for it.
                ["avatar"] = CoworkAvatar(id),
                ["emote"] = CoworkEmote(id),
            });
        }

        AddBridgeSeats(arr);
        return arr;
    }

    /// <summary>
    /// Unity and Unreal get desks too, and they have to come from somewhere
    /// else entirely.
    ///
    /// A bridge is the OPPOSITE of an agent: an agent connects IN and
    /// announces itself in the MCP handshake, which is what writes presence. A
    /// bridge is something the brain calls OUT to, so it never announces
    /// anything and never writes a presence file — which is exactly why the
    /// Unity node on the old bus card could never light up, and why a room
    /// built only from presence/ would have no seat for either of them.
    ///
    /// The roster is mcp-bridges.json (what is configured, and switched on)
    /// and the liveness is whatever the bridge last published under
    /// agent-bus/bridges/&lt;id&gt;/. A configured bridge that has published
    /// nothing still gets a desk: an empty chair with a nameplate is the
    /// honest picture of "wired up, not running".
    /// </summary>
    private void AddBridgeSeats(JArray arr)
    {
        JObject cfg;
        try
        {
            var p = Path.Combine(_vaultPath, ".obsidianx", "mcp-bridges.json");
            if (!File.Exists(p)) return;
            cfg = JObject.Parse(File.ReadAllText(p));
        }
        catch { return; }

        foreach (var (id, def) in EnumerateBridges(cfg))
        {
            if (def["enabled"]?.ToObject<bool?>() != true) continue;
            if (arr.Any(a => string.Equals(a["id"]?.ToString(), id, StringComparison.OrdinalIgnoreCase))) continue;

            var st = LatestBridgeStatus(id);
            var connected = st?["connected"]?.ToObject<bool?>() == true;
            // Tri-state, and the middle one matters: null means no probe is
            // configured, and "I did not ask" must never be drawn as "it said
            // no". Only an explicit false puts the engine out of the room.
            var reachable = st?["reachable"]?.ToObject<bool?>();
            var fresh = st != null
                && DateTime.TryParse(st["lastSeenUtc"]?.ToString(), null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out var seen)
                && (DateTime.UtcNow - seen).TotalSeconds <= (st["ttlSeconds"]?.ToObject<double?>() ?? 120);

            var state = (fresh && connected && reachable != false) ? "idle" : "offline";

            arr.Add(new JObject
            {
                ["id"] = id,
                ["label"] = id,
                ["state"] = state,
                ["lastTool"] = st?["lastTool"]?.ToString() ?? "",
                ["pending"] = 0,
                ["spawned"] = false,
                ["bridge"] = true,
                ["avatar"] = CoworkAvatar(id),
                ["emote"] = CoworkEmote(id),
            });
        }
    }

    /// <summary>The bridges in the config, however that file spells them.</summary>
    private static IEnumerable<(string Id, JObject Def)> EnumerateBridges(JObject cfg)
    {
        foreach (var key in new[] { "bridges", "servers", "mcpServers" })
        {
            if (cfg[key] is JArray list)
            {
                foreach (var t in list.OfType<JObject>())
                    if (t["id"]?.ToString() is { Length: > 0 } id) yield return (id, t);
                yield break;
            }
            if (cfg[key] is JObject map)
            {
                foreach (var (k, v) in map)
                    if (v is JObject o && !k.StartsWith("_") && !k.StartsWith("$")) yield return (k, o);
                yield break;
            }
        }
    }

    /// <summary>
    /// The freshest status a bridge published. One file PER PROCESS, because
    /// several agents can hold a bridge to the same engine open at once — so
    /// the newest write is the one that describes the engine now.
    /// </summary>
    private JObject? LatestBridgeStatus(string id)
    {
        try
        {
            var dir = Path.Combine(CoworkBusRoot, "bridges", id);
            if (!Directory.Exists(dir)) return null;
            var newest = new DirectoryInfo(dir).GetFiles("*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            return newest == null ? null : JObject.Parse(File.ReadAllText(newest.FullName));
        }
        catch { return null; }
    }

    private readonly Dictionary<string, string> _coworkCalls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"pid:calls|pid:calls" over this agent's live sessions, or null
    /// when none publish themselves (an older brainx-mcp).</summary>
    private string? CoworkCallSignature(string agent)
    {
        try
        {
            var dir = Path.Combine(CoworkBusRoot, "presence", "sessions");
            if (!Directory.Exists(dir)) return null;
            var parts = new List<string>();
            foreach (var f in Directory.GetFiles(dir, agent + ".*.json"))
            {
                if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalSeconds > 90) continue;
                try
                {
                    var o = JObject.Parse(File.ReadAllText(f));
                    if (!string.Equals(o["agent"]?.ToString(), agent, StringComparison.OrdinalIgnoreCase)) continue;
                    parts.Add($"{o["pid"]}:{o["calls"]}");
                }
                catch { }
            }
            if (parts.Count == 0) return null;
            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts);
        }
        catch { return null; }
    }

    /// <summary>The agent's chosen appearance, if it has set one. A missing
    /// file is not an error — the room derives a stable look from the name,
    /// so an agent that has never called agent_avatar still has a face.</summary>
    private JToken CoworkAvatar(string agent)
    {
        try
        {
            var p = Path.Combine(CoworkBusRoot, "avatars", agent + ".json");
            if (File.Exists(p)) return JObject.Parse(File.ReadAllText(p));
        }
        catch { }
        return JValue.CreateNull();
    }

    /// <summary>A live emote, or null once it has expired. Expiry is checked
    /// on read so nothing has to run to clear it — an agent that crashed
    /// mid-cheer is not still cheering ten minutes later.</summary>
    private JToken CoworkEmote(string agent)
    {
        try
        {
            var p = Path.Combine(CoworkBusRoot, "avatars", agent + ".emote.json");
            if (!File.Exists(p)) return JValue.CreateNull();
            var o = JObject.Parse(File.ReadAllText(p));
            if (DateTime.TryParse(o["expiresUtc"]?.ToString(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var exp)
                && exp < DateTime.UtcNow) return JValue.CreateNull();
            return o;
        }
        catch { return JValue.CreateNull(); }
    }

    private int CoworkPending(string agent)
    {
        try
        {
            var d = Path.Combine(CoworkBusRoot, "inbox", agent);
            return Directory.Exists(d) ? Directory.GetFiles(d, "*.json").Length : 0;
        }
        catch { return 0; }
    }

    /// <summary>Did the broker start this one, rather than the owner? The run
    /// record says so, and it is the difference between "you left this open"
    /// and "the room hired somebody while you were away".</summary>
    private bool CoworkWasSpawned(string agent)
    {
        try
        {
            var p = Path.Combine(CoworkBusRoot, "broker", agent + ".state.json");
            if (!File.Exists(p)) return false;
            return JObject.Parse(File.ReadAllText(p))["runPid"]?.Type != JTokenType.Null;
        }
        catch { return false; }
    }

    /// <summary>
    /// The conversation. Both folders, because agent_inbox MOVES a message to
    /// read/ when it is delivered — a room that showed only inbox/ would empty
    /// itself exactly as the agents got to work.
    /// </summary>
    private JArray CoworkMessages()
    {
        var rows = new List<(DateTime Ts, string Name, JObject O)>();
        var dir = Path.Combine(CoworkBusRoot, "cowork", "messages");
        if (Directory.Exists(dir))
        {
            IEnumerable<FileInfo> files;
            try
            {
                // Newest-first to take the tail cheaply, then put back in wall
                // order below. The file NAME is ticks-ordered, but a copied or
                // restored vault can carry misleading write times, so the sort
                // that matters is the one on `ts` at the end.
                files = new DirectoryInfo(dir).EnumerateFiles("*.json")
                    .OrderByDescending(f => f.Name, StringComparer.Ordinal).Take(CoworkKeep);
            }
            catch { files = Array.Empty<FileInfo>(); }

            foreach (var f in files)
            {
                JObject o;
                try { o = JObject.Parse(File.ReadAllText(f.FullName)); } catch { continue; }
                // CoworkUtc, not ToString(): `ts` parses into a Date token, and
                // its culture rendering drops the fraction of a second — two lines
                // in the same second then sorted by whatever order they were read
                // in, which put the broker's "who is that?" above the boss's order.
                var ts = CoworkUtc(o["ts"]) ?? f.LastWriteTimeUtc;

                // The boss's chair is only for lines this window sealed — and
                // wrote under this name, with nothing attached: the same test
                // brainx-mcp applies, so the window never seats a line the agents
                // were told is a stranger's.
                var from = o["from"]?.ToString() ?? "?";
                if (from.Equals("owner", StringComparison.OrdinalIgnoreCase)
                    && BrainX.Core.Services.BusSeal.IsActive()
                    && (!BrainX.Core.Services.BusSeal.Verify(o)
                        || !BrainX.Core.Services.BusSeal.WrittenAs(o, f.Name)
                        || o["attachments"] is JArray { Count: > 0 }))
                    from = "owner?";

                var local = ts.ToLocalTime();
                rows.Add((ts, f.Name, new JObject
                {
                    ["id"] = o["id"]?.ToString() ?? f.Name,
                    // The day too, once it is not today: the room keeps eighty lines
                    // and they cross midnight — 23:50 yesterday read as just now.
                    ["ts"] = local.ToString(local.Date == DateTime.Today ? "HH:mm" : "d/M HH:mm",
                                            System.Globalization.CultureInfo.InvariantCulture),
                    ["at"] = new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    ["from"] = from,
                    // A room line is said to the room. `to` is a mention when
                    // there is one, and the page uses it for "→ codex" only.
                    ["to"] = o["to"]?.ToString() is { Length: > 0 } t2 ? t2 : "",
                    ["topic"] = o["work"]?.ToString() ?? o["topic"]?.ToString() ?? "",
                    ["body"] = Trim(o["body"]?.ToString() ?? "", 1200),
                    ["attachments"] = o["attachments"] ?? new JArray(),
                    // Nothing in the room is "pending": it is said, and it
                    // stays said. Unread is a per-agent cursor, not a property
                    // of the line, so the room no longer paints one.
                    ["pending"] = false,
                }));
            }
        }

        var ordered = rows.OrderBy(r => r.Ts).ThenBy(r => r.Name, StringComparer.Ordinal).Select(r => r.O).ToList();
        if (ordered.Count > CoworkKeep) ordered = ordered.Skip(ordered.Count - CoworkKeep).ToList();
        return new JArray(ordered);
    }

    private JArray CoworkDecisions()
    {
        var arr = new JArray();
        var dir = Path.Combine(CoworkBusRoot, "broker", "decisions");
        if (!Directory.Exists(dir)) return arr;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var o = JObject.Parse(File.ReadAllText(f));
                if (!string.Equals(o["status"]?.ToString(), "open", StringComparison.OrdinalIgnoreCase)) continue;

                // Answered, but not yet closed. CoworkAnswer writes `answer`
                // and leaves `status` alone on purpose — the broker owns the
                // transition and the delivery back to the agent — so between
                // the click and the broker's next tick this is still "open",
                // and the card came straight back. With the broker stopped it
                // came back forever. One press is the whole contract.
                if (!string.IsNullOrWhiteSpace(o["answer"]?.ToString())) continue;

                arr.Add(o);
            }
            catch { }
        }
        return arr;
    }

    // ───────────── what the room asks for ─────────────

    private void OnCoworkMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var m = JObject.Parse(e.WebMessageAsJson);
            switch (m["type"]?.ToString())
            {
                case "officeReady": PostCowork(); break;
                case "officeSay":
                    // Said out loud when it fails. The page has already cleared
                    // the box and shown the boss speaking; swallowing this left
                    // the owner believing an order went out that is not on disk.
                    try { CoworkSay(m["text"]?.ToString()); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"CoworkSay: {ex.Message}");
                        CoworkSurface?.PostWebMessageAsJson(new JObject
                        {
                            ["type"] = "officeSayFailed",
                            ["text"] = m["text"]?.ToString() ?? "",
                            ["reason"] = ex is IOException or UnauthorizedAccessException
                                ? "เขียนลงโฟลเดอร์ของห้องไม่ได้ (ไดรฟ์ของ vault อาจหลุดหรือถูกล็อก)"
                                : ex.Message,
                        }.ToString());
                    }
                    break;
                // "Call them in" from the board. The page names the task and
                // nothing else; the line is built here from the task FILE, because
                // its title was written by an agent and the line goes out sealed.
                case "officeCall": CoworkCallForTask(m["task"]?.ToString()); break;
                case "officeAnswer": CoworkAnswer(m["id"]?.ToString(), m["answer"]?.ToString()); break;
                // The room's switch for the boss. Handled HERE rather than in
                // the page for the same reason the owner's line is: starting
                // and stopping a process that spawns agents belongs to a
                // process the owner controls, not to a document.
                case "officeBroker": ToggleBrokerHost(); break;
                // The light. Off sends everybody home and stops the room
                // talking to itself; on lets them back in.
                case "officeRoomLight":
                    CoworkSetRoomLight(m["on"]?.ToObject<bool>() ?? true, "owner");
                    if (m["on"]?.ToObject<bool>() == false)
                        CoworkWriteRoomLine("broker", "🔌 บอสปิดไฟปิดห้อง — ทุกคนออกจากห้องแล้ว", "room");
                    PostCowork();
                    break;
                // Installing or removing the Windows Service. Elevation is
                // asked for by the client, never by the page.
                case "officeBrokerService":
                    var verb = m["action"]?.ToString();
                    // No "install": the service ran as SYSTEM, which cannot reach
                    // the owner's agents and ran a user-writable exe with SYSTEM
                    // rights. Retired 2026-09-21; removing an old one stays.
                    if (verb is "uninstall" or "stop") RunBrokerServiceVerb(verb);
                    break;
                case "officeOpen": CoworkOpen(m["path"]?.ToString()); break;
                case "officeModel": CoworkSetModel(m["agent"]?.ToString(), m["model"]?.ToString()); break;
                case "officeEffort": CoworkSetEffort(m["agent"]?.ToString(), m["effort"]?.ToString()); break;
                // The work window: pause / resume / drop / focus one piece of
                // work, or stop a run the boss started (MainWindow.CoworkControl).
                case "officeTask": CoworkTaskControl(m["task"]?.ToString(), m["action"]?.ToString()); break;
                case "officeStopRun": CoworkStopRunFromRoom(m["agent"]?.ToString()); break;
                // The room's history by project: read, and remove finished work
                // from it (MainWindow.CoworkHistory) — never from the brain.
                case "officeHistory": PostCoworkHistory(HistStr(m["project"])); break;
                case "officeHistorySummary": SecretarySummarize(HistStr(m["project"])); break;
                case "officeSecretary": SecretaryChange(m); break;
                case "officeHistoryDelete":
                    CoworkHistoryDelete(HistStr(m["project"]), HistStr(m["task"]), HistStr(m["show"]));
                    break;
                // The room in a window of its own, and back (MainWindow.CoworkPopout).
                case "officePopOut": _ = OpenCoworkWindowAsync(); break;
                // Posted from inside the window being closed: let this event
                // return before its WebView goes away.
                case "officeDock": Dispatcher.BeginInvoke(new Action(DockCoworkWindow)); break;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cowork msg: {ex.Message}"); }
    }

    /// <summary>
    /// The owner speaks, and everyone IN THE ROOM hears it.
    ///
    /// Owner (2026-09-20): "ให้คำสั่งที่ผ่านระบบ cowork ไม่ไปปะปนกับระบบแชทเดิมเลย
    /// ไม่ต้องส่งเมลไป ... เป็นคนละเลนไปเลย".
    ///
    /// This used to fan out one piece of MAIL per live agent, and that is
    /// exactly what made the room unusable: mail goes to whichever session of
    /// a vendor opens the inbox first, and every session sees the unread
    /// notice whether or not it has anything to do with the room. A line typed
    /// at the office door surfaced in the middle of unrelated work.
    ///
    /// Now it is ONE line on the room's own wall — cowork/messages/ — written
    /// as `owner`, an identity brainx-mcp reserves so no handshake can claim
    /// it. Only sessions that called cowork_join are told, and a session that
    /// never joined hears nothing, ever. That is the separate lane.
    /// </summary>
    private void CoworkSay(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        // The owner's words, unwrapped. The old fan-out wrapped every line in
        // a paragraph explaining what the room was and how to answer it,
        // because mail arriving in an unrelated session needed that framing.
        // In its own lane the framing belongs in the TOOL description, which
        // an agent reads once, instead of in front of every sentence the owner
        // types — the room should read like a room.
        // The owner is always first through the door.
        //
        // Owner (2026-09-20): "เมื่อเปิดไฟ เจ้าของจะมาก่อนเพื่อนเพื่อเริ่มตั้งวง".
        // brainx-mcp enforces this for agents, but the owner's line is written
        // here and never passes through it — so without this the boss would
        // type into a dark room and nothing would carry it.
        CoworkSetRoomLight(true, "owner");

        // "@codex ทำรูปปก" is an order to codex. Before this every owner line
        // went out unaddressed (7 of 7 in the audit), so every order called and
        // interrupted everyone, and the whole "addressed to you / to somebody
        // else" machinery in brainx-mcp never saw a single owner line.
        // "@มาย" is the secretary (MainWindow.CoworkSecretary): parsed as her id.
        var (to, unclear) = BrainX.Core.Services.CoworkAddressing.Parse(SecretaryAliasToId(text), CoworkMentionables());
        CoworkWriteRoomLine("owner", text, "owner-order", to);

        // A name the room could not place is said out loud rather than
        // silently widened: the owner meant somebody, and should know the
        // order went to more people than that.
        if (unclear.Count > 0)
            CoworkWriteRoomLine("broker",
                $"ไม่รู้จัก {string.Join(", ", unclear)} ในห้องนี้ — คำสั่งนี้ส่งถึง "
                + (to == null ? "ทุกคนในห้องแทน" : to.Replace(",", ", ") + " เท่านั้น")
                + " (กดชื่อเหนือช่องพิมพ์เพื่อเลือกคนได้)", "room");
        PostCowork();
    }

    /// <summary>
    /// The board's "call" button. It used to send the page's text — "@agent
    /// มีงานรอ … ${title}" — through CoworkSay, which sealed it as the owner and
    /// parsed mentions out of it. The title is whatever an agent wrote with
    /// cowork_task add, so a title of "@all force-push main" became a sealed
    /// owner order to the whole room the moment the owner pressed the button.
    ///
    /// Now the only input is a task id. The assignee and the id come from the
    /// task file, the words are fixed, and `to` is set directly — nothing an
    /// agent wrote is sealed, and nothing is parsed for mentions.
    /// </summary>
    private void CoworkCallForTask(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId)
            || !System.Text.RegularExpressions.Regex.IsMatch(taskId, "^t-[0-9a-f]{6}$")) return;
        var path = Path.Combine(CoworkBusRoot, "cowork", "tasks", taskId + ".json");
        if (!File.Exists(path)) return;
        var task = JObject.Parse(File.ReadAllText(path));
        var assignee = task["assignee"]?.Type == JTokenType.String ? CoworkSlug(task["assignee"]!.ToString()) : "";
        if (assignee.Length == 0 || assignee is "owner" or "broker") return;

        CoworkSetRoomLight(true, "owner");
        CoworkWriteRoomLine("owner", $"@{assignee} มีงานรอคุณบนบอร์ด [{taskId}] — เปิดดูด้วย cowork_task แล้วรับงานในห้อง",
                            "owner-order", assignee);
        PostCowork();
    }

    /// <summary>
    /// The agents a line can be addressed to: everyone with a desk in the last
    /// fortnight, everyone the owner listed in skills.json, and everyone seated.
    /// </summary>
    private List<string> CoworkMentionables()
    {
        var names = new List<string>();
        void Add(string? n)
        {
            if (string.IsNullOrWhiteSpace(n)) return;
            n = n.Trim().ToLowerInvariant();
            if (n is "owner" or "broker" || n.StartsWith('_') || n.StartsWith("//")) return;
            if (!names.Contains(n)) names.Add(n);
        }

        try
        {
            var presence = Path.Combine(CoworkBusRoot, "presence");
            if (Directory.Exists(presence))
                foreach (var f in Directory.GetFiles(presence, "*.json"))
                    if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalDays <= 14)
                        Add(Path.GetFileNameWithoutExtension(f));

            var skills = Path.Combine(CoworkBusRoot, "cowork", "skills.json");
            if (File.Exists(skills))
                foreach (var (k, _) in JObject.Parse(File.ReadAllText(skills)))
                    Add(k);

            var members = Path.Combine(CoworkBusRoot, "cowork", "members");
            if (Directory.Exists(members))
                foreach (var f in Directory.GetFiles(members, "*.json"))
                    Add(Path.GetFileNameWithoutExtension(f));

            // The secretary, while she is on.
            if (SecretaryOn) Add("mind");
        }
        catch { /* a mention that cannot be resolved just goes to the room */ }
        return names;
    }

    /// <summary>
    /// The board: every task still open or in progress, and what closed in the
    /// last day. Read straight from cowork/tasks/ — the agents write those files
    /// through cowork_task, and this window only draws them.
    /// </summary>
    private JArray CoworkTasks()
    {
        var arr = new JArray();
        var dir = Path.Combine(CoworkBusRoot, "cowork", "tasks");
        if (!Directory.Exists(dir)) return arr;

        var rows = new List<(int Order, DateTime Created, JObject Row)>();
        var order = new[] { "blocked", "assigned", "open", "doing", "done", "dropped" };
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            JObject o;
            try { o = JObject.Parse(File.ReadAllText(f)); } catch { continue; }

            var status = o["status"]?.ToString() ?? "open";
            var updated = CoworkUtc(o["updatedUtc"]) ?? File.GetLastWriteTimeUtc(f);
            var finished = status is "done" or "dropped";
            if (finished && (DateTime.UtcNow - updated).TotalHours > 24) continue;

            rows.Add((Array.IndexOf(order, status), CoworkUtc(o["createdUtc"]) ?? updated, new JObject
            {
                ["id"] = o["id"]?.ToString() ?? Path.GetFileNameWithoutExtension(f),
                ["title"] = Trim(o["title"]?.ToString() ?? "", 200),
                ["status"] = status,
                ["assignee"] = o["assignee"]?.Type == JTokenType.String ? o["assignee"]!.ToString() : "",
                ["createdBy"] = o["createdBy"]?.ToString() ?? "",
                ["note"] = Trim(o["note"]?.ToString() ?? "", 300),
                ["at"] = new DateTimeOffset(DateTime.SpecifyKind(updated, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                // "owner" = paused from the work window, "quota" = out of quota.
                ["paused"] = (o["paused"] as JObject)?["reason"]?.ToString() ?? "",
                ["droppedBy"] = o["droppedBy"]?.ToString() ?? "",
            }));
        }
        foreach (var r in rows.OrderBy(r => r.Order).ThenBy(r => r.Created)) arr.Add(r.Row);
        return arr;
    }

    /// <summary>A timestamp from one of the room's files, as UTC — type first,
    /// because a Date token's ToString() is rendered in the Thai calendar.</summary>
    private static DateTime? CoworkUtc(JToken? t)
    {
        if (t == null || t.Type == JTokenType.Null) return null;
        if (t.Type == JTokenType.Date) return t.ToObject<DateTime>().ToUniversalTime();
        return DateTime.TryParse(t.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                   out var d) ? d : null;
    }

    /// <summary>
    /// One line on the room's wall.
    ///
    /// Same on-disk shape brainx-mcp's cowork_say writes, so the agents' own
    /// lines and the owner's are the same kind of object and every reader —
    /// the room, cowork_read, the cursor logic — treats them identically.
    /// Nothing here is addressed to anybody: a room has one transcript, and
    /// who is listening is decided by who joined, not by who was written to.
    /// </summary>
    /// <summary>
    /// The light switch, from the owner's side.
    ///
    /// Dark is what stops a circle of agents talking to each other until the
    /// tokens run out: no seats, no notices, nobody called in. Turning it back
    /// on seats nobody — the room fills as agents say hello.
    /// </summary>
    private void CoworkSetRoomLight(bool on, string by)
    {
        try
        {
            var dir = Path.Combine(CoworkBusRoot, "cowork");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "room.json");

            if (on && CoworkRoomIsOpen()) return;   // already lit; leave the reason alone

            var payload = new JObject
            {
                ["open"] = on,
                ["sinceUtc"] = DateTime.UtcNow.ToString("o"),
                ["by"] = by,
            };
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, payload.ToString(), new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);

            // Everybody out. Seats are removed rather than tombstoned: leaving
            // is an agent's own decision and should survive; being sent home
            // when the room closes is not.
            // A leave tombstone stays: leaving was the agent's own decision,
            // and deleting it here put the agent back in the room next morning.
            if (!on)
            {
                var members = Path.Combine(dir, "members");
                if (Directory.Exists(members))
                    foreach (var f in Directory.GetFiles(members, "*.json"))
                    {
                        try
                        {
                            if (JObject.Parse(File.ReadAllText(f))["optedOut"]?.ToObject<bool?>() == true) continue;
                        }
                        catch { /* unreadable: it is not a tombstone anybody can honour */ }
                        try { File.Delete(f); } catch { }
                    }
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CoworkSetRoomLight: {ex.Message}"); }
    }

    /// <summary>Is the room lit? A missing file means yes — a room that went
    /// dark the moment this shipped would look exactly like a bug.</summary>
    private bool CoworkRoomIsOpen()
    {
        try
        {
            var path = Path.Combine(CoworkBusRoot, "cowork", "room.json");
            if (!File.Exists(path)) return true;
            return JObject.Parse(File.ReadAllText(path))["open"]?.ToObject<bool?>() != false;
        }
        catch { return true; }
    }

    private void CoworkWriteRoomLine(string from, string body, string topic, string? to = null)
    {
        var dir = Path.Combine(CoworkBusRoot, "cowork", "messages");
        Directory.CreateDirectory(dir);
        var payload = new JObject
        {
            ["id"] = $"c-{DateTime.UtcNow.Ticks}-{Guid.NewGuid().ToString("N")[..6]}",
            ["ts"] = DateTime.UtcNow.ToString("o"),
            ["from"] = from,
            ["fromClient"] = "brainx-cowork",
            ["topic"] = topic,
            ["body"] = body,
        };
        // Before the seal, never after: the seal covers `to`, so an order
        // cannot be readdressed on disk without breaking it.
        if (!string.IsNullOrEmpty(to)) payload["to"] = to;

        // The owner's words carry the owner's seal — without it the MCP treats
        // a line as a peer's, whatever its `from` says (see BusSeal).
        //
        // And without a seal the line is not written at all. It used to go out
        // unsealed after a logged error, and brainx-mcp then showed the boss's
        // order to every agent as "unverified-owner" — a peer line — with
        // nobody told why the room had stopped obeying.
        if (from == "owner") BrainX.Core.Services.BusSeal.Seal(payload);

        // temp + move, the same atomic write the bus uses everywhere: a reader
        // polling this directory must never see half a JSON document.
        var name = $"{DateTime.UtcNow.Ticks:D19}-{from}-{Guid.NewGuid().ToString("N")[..4]}.json";
        var tmp = Path.Combine(dir, name + ".tmp");
        File.WriteAllText(tmp, payload.ToString(), new System.Text.UTF8Encoding(false));
        File.Move(tmp, Path.Combine(dir, name));
    }

    /// <summary>
    /// The boss, as the room sees it: who is dispatching, and whether the
    /// Windows Service is there to carry on when this app closes.
    ///
    /// `sc query` costs a process, so it is read on a slower clock than the
    /// room's two-second poll — the service's state changes when somebody
    /// installs or stops it, not between two frames of an animation.
    /// </summary>
    private DateTime _coworkServiceCheckedUtc;
    /// <summary>Until when the service is read on every poll instead of every
    /// twenty seconds — set by an install/start/stop so its result shows up.</summary>
    private DateTime _coworkServiceFastUntilUtc;
    private BrokerServiceInfo _coworkServiceCache = BrokerServiceInfo.Unknown;
    private int _coworkServiceReading;

    private JObject CoworkBrokerState()
    {
        // Read off the dispatcher: this runs on the room's UI timer, and the
        // read is two sc.exe processes plus a handle open — a stall the window
        // would feel on every poll while a UAC prompt is being answered. The
        // room draws the last answer and gets the new one on the next tick.
        var every = DateTime.UtcNow < _coworkServiceFastUntilUtc ? 2 : 20;
        if ((DateTime.UtcNow - _coworkServiceCheckedUtc).TotalSeconds > every
            && Interlocked.CompareExchange(ref _coworkServiceReading, 1, 0) == 0)
        {
            _coworkServiceCheckedUtc = DateTime.UtcNow;
            _ = Task.Run(() =>
            {
                // One read at a time: sc.exe can take seconds on a busy
                // machine, and the fast two-second clock must not stack them.
                try { _coworkServiceCache = BrokerServiceStatus(); }
                finally { Interlocked.Exchange(ref _coworkServiceReading, 0); }
            });
        }
        var s = _coworkServiceCache;
        return new JObject
        {
            ["state"] = BrokerState,
            ["tail"] = new JArray(BrokerTail(8)),
            ["service"] = new JObject
            {
                ["installed"] = s.Installed,
                ["state"] = s.State,
                ["account"] = s.Account,
                ["privileged"] = s.Privileged,
                ["canControl"] = s.CanControl,
                ["neverStarted"] = s.NeverStarted,
                ["userWritableBinary"] = s.UserWritableBinary,
                ["last"] = _brokerServiceLast is { } last
                    ? new JObject
                    {
                        ["action"] = last.Action,
                        ["result"] = last.Result,
                        ["atUtc"] = last.AtUtc.ToString("o"),
                    }
                    : null,
            },
        };
    }

    /// <summary>Did this agent join the room? One file, written by cowork_join
    /// and removed by cowork_leave.</summary>
    private bool CoworkInRoom(string agent)
    {
        try
        {
            var p = Path.Combine(CoworkBusRoot, "cowork", "members", agent + ".json");
            // The file is also what cowork_leave leaves behind. Counting that
            // drew an agent who had left as listening, and hid the board's
            // "call" button for its work.
            return File.Exists(p) && JObject.Parse(File.ReadAllText(p))["optedOut"]?.ToObject<bool?>() != true;
        }
        catch { return false; }
    }

    private IEnumerable<string> CoworkLiveAgents()
    {
        var dir = Path.Combine(CoworkBusRoot, "presence");
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(f);
            if (id is "owner" or "broker") continue;
            double age;
            try
            {
                var o = JObject.Parse(File.ReadAllText(f));
                var seen = DateTime.Parse(o["lastSeenUtc"]?.ToString() ?? "", null,
                    System.Globalization.DateTimeStyles.RoundtripKind);
                age = (DateTime.UtcNow - seen).TotalSeconds;
            }
            catch { continue; }
            if (age <= 90) yield return id;
        }
    }

    /// <summary>
    /// Write a message in exactly the on-disk shape agent_send produces, so
    /// every reader — agent_inbox, the wake hooks, the broker, the cards —
    /// treats it as an ordinary message and none of them need to know the
    /// client wrote it.
    /// </summary>
    private void CoworkWriteBusMessage(string from, string to, string body, string topic)
    {
        var inbox = Path.Combine(CoworkBusRoot, "inbox", to);
        Directory.CreateDirectory(inbox);
        var payload = new JObject
        {
            ["id"] = $"m-{DateTime.UtcNow.Ticks}-{Guid.NewGuid():N}".Substring(0, 24),
            ["ts"] = DateTime.UtcNow.ToString("o"),
            ["from"] = from,
            ["fromClient"] = "brainx-cowork",
            ["to"] = to,
            ["topic"] = topic,
            ["body"] = body,
        };

        // temp + move, the same atomic write the bus uses everywhere: a reader
        // polling this directory must never see half a JSON document.
        var name = $"{DateTime.UtcNow.Ticks:D19}-{from}-{Guid.NewGuid().ToString("N")[..4]}.json";
        var tmp = Path.Combine(inbox, name + ".tmp");
        File.WriteAllText(tmp, payload.ToString(), new System.Text.UTF8Encoding(false));
        File.Move(tmp, Path.Combine(inbox, name));
    }

    private void CoworkAnswer(string? id, string? answer)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(answer)) return;
        try
        {
            var path = Path.Combine(CoworkBusRoot, "broker", "decisions",
                Path.GetFileName(id) + ".json");
            if (!File.Exists(path)) return;
            var o = JObject.Parse(File.ReadAllText(path));
            // Only `answer` is written. The broker owns the status transition
            // and the delivery back to the agent; setting status here would
            // close the question without anybody being told the answer.
            o["answer"] = answer;
            o["answeredVia"] = "cowork";
            // temp + move: the broker reads and rewrites this file on its own
            // clock, and a half-written answer is a lost one.
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
            File.WriteAllText(tmp, o.ToString(), new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            PostCowork();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CoworkAnswer: {ex.Message}"); }
    }

    // ───────────── which model the boss starts each agent on ─────────────

    /* Owner (2026-10-04): "ให้เลือกโมเดลที่ซัพพอตเพื่อเข้าทำงานได้ มีตัวเลือก".
     *
     * Only for runs the BROKER starts — an order in an empty room, an idle
     * study. A session the owner opened runs on whatever its own app is set
     * to, and nothing here can change that. The pick lands in
     * cowork/models.json, which the broker reads at every spawn: no restart.
     */

    private (string Path, DateTime Stamp, JObject? Runners) _coworkRunnersCache;

    /// <summary>runners.json's runners, re-read only when the file changes —
    /// this is asked every two seconds.</summary>
    private JObject? CoworkRunners()
    {
        try
        {
            var p = Path.Combine(CoworkBusRoot, "runners.json");
            if (!File.Exists(p)) return null;
            var stamp = File.GetLastWriteTimeUtc(p);
            // Keyed by path as well: a vault switch is a different file.
            if (_coworkRunnersCache.Stamp != stamp || _coworkRunnersCache.Path != p)
            {
                JObject? runners = null;
                try { runners = JObject.Parse(File.ReadAllText(p))["runners"] as JObject; }
                catch { /* the owner's file, mid-edit; the broker logs it */ }
                _coworkRunnersCache = (p, stamp, runners);
            }
            return _coworkRunnersCache.Runners;
        }
        catch { return null; }
    }

    /// <summary>The agent name a runners.json key becomes — the same folding
    /// brainx-mcp's SanitizeAgentSlug does, so a pick is filed under the name
    /// the broker looks it up by.</summary>
    private static string CoworkSlug(string raw)
    {
        var slug = new string(raw.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return slug.Length > 32 ? slug[..32].Trim('-') : slug;
    }

    /// <summary>One row per runner: what it can be started on, what the owner
    /// picked, and what a run already in flight was started on.</summary>
    private JArray CoworkModels()
    {
        var arr = new JArray();
        var runners = CoworkRunners();
        if (runners == null) return arr;
        var picks = BrainX.Core.Services.RunnerModels.ReadChoices(CoworkBusRoot);
        var effortPicks = BrainX.Core.Services.RunnerModels.ReadEffortChoices(CoworkBusRoot);

        foreach (var (name, val) in runners)
        {
            if (val is not JObject r || name.StartsWith("//", StringComparison.Ordinal)) continue;
            try
            {
                var agent = CoworkSlug(name);
                if (agent.Length == 0) continue;
                var args = (r["args"] as JArray)?.Select(t => t.ToString()).ToList() ?? new List<string>();
                var flag = r["modelFlag"]?.Type == JTokenType.String
                    ? r["modelFlag"]!.ToString().Trim()
                    : BrainX.Core.Services.RunnerModels.DefaultFlag;
                var options = BrainX.Core.Services.RunnerModels.Supported(
                    agent, r["exe"]?.ToString() ?? "", r["models"], out var cliDefault);
                var configured = r["model"]?.Type == JTokenType.String
                                 && BrainX.Core.Services.RunnerModels.IsValidId(r["model"]!.ToString().Trim())
                    ? r["model"]!.ToString().Trim() : "";
                var chosen = picks.TryGetValue(agent, out var pick) ? pick : "";

                // The levels of the model the NEXT run starts on — the pick,
                // else runners.json's, else the CLI's — so changing the model
                // above changes what this list offers.
                var effortArgs = r["effortArgs"] != null
                    ? (r["effortArgs"] as JArray)?.Select(t => t.ToString()).ToList() ?? new List<string> { r["effortArgs"]!.ToString() }
                    : BrainX.Core.Services.RunnerModels.DefaultEffortArgs(agent, r["exe"]?.ToString() ?? "").ToList();
                var nextModel = chosen.Length > 0 ? chosen : configured.Length > 0 ? configured : null;
                var efforts = BrainX.Core.Services.RunnerModels.SupportedEfforts(
                    agent, r["exe"]?.ToString() ?? "", nextModel, r["efforts"], out var effortDefault);
                var effortConfigured = r["effort"]?.Type == JTokenType.String
                                       && BrainX.Core.Services.RunnerModels.IsValidEffort(r["effort"]!.ToString().Trim())
                    ? r["effort"]!.ToString().Trim() : "";
                var (runModel, runEffort) = CoworkRunModel(agent);

                arr.Add(new JObject
                {
                    ["agent"] = agent,
                    ["canChoose"] = BrainX.Core.Services.RunnerModels.CanChoose(args, flag),
                    ["chosen"] = chosen,
                    ["configured"] = configured,
                    ["cliDefault"] = cliDefault ?? "",
                    ["onCall"] = r["onCall"]?.Type == JTokenType.Boolean && r["onCall"]!.ToObject<bool>(),
                    ["running"] = runModel ?? "",
                    ["options"] = Options(options),
                    ["effortCanChoose"] = BrainX.Core.Services.RunnerModels.CanChooseEffort(args, effortArgs),
                    ["effortChosen"] = effortPicks.TryGetValue(agent, out var effortPick) ? effortPick : "",
                    ["effortConfigured"] = effortConfigured,
                    ["effortCliDefault"] = effortDefault ?? "",
                    ["effortRunning"] = runEffort ?? "",
                    ["effortOptions"] = Options(efforts),
                });
            }
            catch { /* one broken runner entry must not hide the others */ }
        }
        return arr;

        static JArray Options(IEnumerable<BrainX.Core.Services.RunnerModels.Option> list) =>
            new(list.Select(o => new JObject { ["id"] = o.Id, ["label"] = o.Label, ["note"] = o.Note }));
    }

    /// <summary>The model and effort a broker run still in flight was started on.</summary>
    private (string? Model, string? Effort) CoworkRunModel(string agent)
    {
        try
        {
            var p = Path.Combine(CoworkBusRoot, "broker", agent + ".state.json");
            if (!File.Exists(p)) return (null, null);
            var o = JObject.Parse(File.ReadAllText(p));
            if (o["runPid"] == null || o["runPid"]!.Type == JTokenType.Null) return (null, null);
            return (o["runModel"]?.Type == JTokenType.String ? o["runModel"]!.ToString() : null,
                    o["runEffort"]?.Type == JTokenType.String ? o["runEffort"]!.ToString() : null);
        }
        catch { return (null, null); }
    }

    /// <summary>
    /// The owner picked a model in the room. Only a value that was OFFERED for
    /// that runner is written: the page is a document, and this string ends up
    /// on the command line of a process with write access to the owner's repos.
    /// Empty clears the pick back to the runner's default.
    /// </summary>
    private void CoworkSetModel(string? agent, string? model)
    {
        agent = agent?.Trim() ?? "";
        model = model?.Trim() ?? "";
        try
        {
            var row = CoworkModels().OfType<JObject>().FirstOrDefault(m =>
                string.Equals(m["agent"]?.ToString(), agent, StringComparison.OrdinalIgnoreCase));
            if (row == null || row["canChoose"]?.ToObject<bool>() != true) return;
            var offered = (row["options"] as JArray ?? new JArray())
                .Any(o => string.Equals(o["id"]?.ToString(), model, StringComparison.Ordinal));
            if (model.Length > 0 && !offered) return;

            BrainX.Core.Services.RunnerModels.WriteChoice(
                CoworkBusRoot, row["agent"]!.ToString(), model.Length == 0 ? null : model);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CoworkSetModel: {ex.Message}"); }
        PostCowork();
    }

    /// <summary>The owner picked an effort level in the room. The same rule as
    /// the model: only a level offered for that runner's next model is written.</summary>
    private void CoworkSetEffort(string? agent, string? effort)
    {
        agent = agent?.Trim() ?? "";
        effort = effort?.Trim() ?? "";
        try
        {
            var row = CoworkModels().OfType<JObject>().FirstOrDefault(m =>
                string.Equals(m["agent"]?.ToString(), agent, StringComparison.OrdinalIgnoreCase));
            if (row == null || row["effortCanChoose"]?.ToObject<bool>() != true) return;
            var offered = (row["effortOptions"] as JArray ?? new JArray())
                .Any(o => string.Equals(o["id"]?.ToString(), effort, StringComparison.Ordinal));
            if (effort.Length > 0 && !offered) return;

            BrainX.Core.Services.RunnerModels.WriteEffortChoice(
                CoworkBusRoot, row["agent"]!.ToString(), effort.Length == 0 ? null : effort);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CoworkSetEffort: {ex.Message}"); }
        PostCowork();
    }

    /// <summary>
    /// Kinds of file opened straight in their viewer. Everything else an agent
    /// attaches is SHOWN in Explorer instead: ShellExecute on a .bat, .js,
    /// .hta or .lnk an agent sent runs it, and one click on the 📎 chip was
    /// all it took. No .svg (script runs in the browser that opens it) and no
    /// .csv (Excel formulas).
    /// </summary>
    private static readonly HashSet<string> CoworkViewable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".pdf", ".txt", ".md", ".log", ".json",
    };

    /// <summary>Open an attachment with whatever the owner normally opens it
    /// with — or point Explorer at it when it is not a kind that is safe to.</summary>
    private void CoworkOpen(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return;
        try
        {
            // Attachments live under files/, and only there. The trailing
            // separator matters: "agent-bus-old\x.exe" starts with "agent-bus".
            var root = Path.GetFullPath(Path.Combine(CoworkBusRoot, "files")).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(CoworkBusRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(full)) return;

            var psi = CoworkViewable.Contains(Path.GetExtension(full))
                ? new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true }
                : new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{full}\"") { UseShellExecute = false };
            System.Diagnostics.Process.Start(psi)?.Dispose();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CoworkOpen: {ex.Message}"); }
    }
}
