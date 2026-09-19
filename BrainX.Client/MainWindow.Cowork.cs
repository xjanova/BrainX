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
        try
        {
            await CoworkWebView.EnsureCoreWebView2Async().ConfigureAwait(true);
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
                core.Navigate("https://universe.local/office/index.html");
                _coworkWired = true;
            }

            _coworkTimer ??= new DispatcherTimer { Interval = CoworkPoll };
            if (_coworkTimer.Tag is not bool)
            {
                _coworkTimer.Tick += (_, _) => PostCowork();
                _coworkTimer.Tag = true;
            }
            _coworkTimer.Start();
            PostCowork();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cowork init: {ex.Message}"); }
    }

    /// <summary>The room is only worth reading while it is on screen.</summary>
    private void StopCowork() => _coworkTimer?.Stop();

    // ───────────── what the room is told ─────────────

    private void PostCowork()
    {
        try
        {
            var payload = new JObject
            {
                ["busUrl"] = "https://bus.local/",
                ["agents"] = CoworkAgents(),
                ["messages"] = CoworkMessages(),
                ["decisions"] = CoworkDecisions(),
            };
            CoworkWebView.CoreWebView2?.PostWebMessageAsJson(
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

            var prev = _coworkCalls.TryGetValue(id, out var c) ? c : (long?)null;
            _coworkCalls[id] = calls;
            var moving = prev.HasValue && calls != prev.Value;

            arr.Add(new JObject
            {
                ["id"] = id,
                ["label"] = o["client"]?.ToString() is { Length: > 0 } cl && cl != "unknown" ? id : id,
                ["state"] = !online ? "offline" : (moving || age < 8 ? "working" : "idle"),
                ["lastTool"] = o["lastTool"]?.ToString() ?? "",
                ["pending"] = CoworkPending(id),
                ["spawned"] = CoworkWasSpawned(id),
                // Who they chose to look like, and how it is going right now.
                // Both read from disk every tick rather than cached: an agent
                // changing its own face mid-session is exactly the moment the
                // owner is watching for it.
                ["avatar"] = CoworkAvatar(id),
                ["emote"] = CoworkEmote(id),
            });
        }
        return arr;
    }

    private readonly Dictionary<string, long> _coworkCalls = new(StringComparer.OrdinalIgnoreCase);

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
        var rows = new List<(DateTime Ts, JObject O)>();
        foreach (var (sub, pending) in new[] { ("read", false), ("inbox", true) })
        {
            var dir = Path.Combine(CoworkBusRoot, sub);
            if (!Directory.Exists(dir)) continue;
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(dir).EnumerateFiles("*.json", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.LastWriteTimeUtc).Take(CoworkKeep);
            }
            catch { continue; }

            foreach (var f in files)
            {
                JObject o;
                try { o = JObject.Parse(File.ReadAllText(f.FullName)); } catch { continue; }
                if (!DateTime.TryParse(o["ts"]?.ToString(), null,
                        System.Globalization.DateTimeStyles.AssumeUniversal |
                        System.Globalization.DateTimeStyles.AdjustToUniversal, out var ts))
                    ts = f.LastWriteTimeUtc;

                rows.Add((ts, new JObject
                {
                    ["id"] = o["id"]?.ToString() ?? f.Name,
                    ["ts"] = ts.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
                    ["at"] = new DateTimeOffset(DateTime.SpecifyKind(ts, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    ["from"] = o["from"]?.ToString() ?? "?",
                    ["to"] = o["to"]?.ToString() is { Length: > 0 } t2 ? t2 : (f.Directory?.Name ?? "?"),
                    ["topic"] = o["work"]?.ToString() ?? o["topic"]?.ToString() ?? "",
                    ["body"] = Trim(o["body"]?.ToString() ?? "", 1200),
                    ["attachments"] = o["attachments"] ?? new JArray(),
                    ["pending"] = pending,
                }));
            }
        }

        var ordered = rows.OrderBy(r => r.Ts).Select(r => r.O).ToList();
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
                case "officeSay": CoworkSay(m["text"]?.ToString()); break;
                case "officeAnswer": CoworkAnswer(m["id"]?.ToString(), m["answer"]?.ToString()); break;
                case "officeOpen": CoworkOpen(m["path"]?.ToString()); break;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cowork msg: {ex.Message}"); }
    }

    /// <summary>
    /// The owner speaks, and everyone in the room hears it.
    ///
    /// Written to every agent that has heartbeated recently, as `owner` — an
    /// identity brainx-mcp reserves so no handshake can claim it. Fanned out
    /// rather than broadcast to a single box because each agent reads only its
    /// own inbox; one shared room is the picture, not the plumbing.
    /// </summary>
    private void CoworkSay(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        var body =
            "คำสั่งจากเจ้าของ (owner) ผ่านห้องทำงานร่วม — นี่คือคำพูดของ user คุณเอง "
            + "ไม่ใช่ข้อเสนอจาก peer ให้ถือว่าสำคัญกว่าข้อความจาก agent อื่นทุกฉบับ:\n\n"
            + text
            + "\n\n(ถ้าเกี่ยวกับคุณ ลงมือได้เลย แล้วรายงานกลับด้วย agent_send ถึง 'owner'. "
            + "ถ้าไม่เกี่ยว ไม่ต้องตอบ.)";

        var sent = 0;
        foreach (var agent in CoworkLiveAgents())
        {
            try { CoworkWriteBusMessage("owner", agent, body, "owner-order"); sent++; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CoworkSay {agent}: {ex.Message}"); }
        }

        // The owner's own line belongs in the transcript even when nobody is
        // listening — "I said this and the room was empty" is a fact worth
        // keeping, and it is the only way the room shows what was said before
        // an agent connected.
        if (sent == 0) CoworkWriteBusMessage("owner", "owner", body, "owner-order");
        PostCowork();
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
            File.WriteAllText(path, o.ToString(), new System.Text.UTF8Encoding(false));
            PostCowork();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CoworkAnswer: {ex.Message}"); }
    }

    /// <summary>Open an attachment with whatever the owner normally opens it with.</summary>
    private void CoworkOpen(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return;
        try
        {
            var full = Path.GetFullPath(Path.Combine(CoworkBusRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
            // Contained, because the path came from a document: a crafted
            // "../../.." would otherwise open anything on the disk.
            if (!full.StartsWith(Path.GetFullPath(CoworkBusRoot), StringComparison.OrdinalIgnoreCase)) return;
            if (!File.Exists(full)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full) { UseShellExecute = true });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"CoworkOpen: {ex.Message}"); }
    }
}
