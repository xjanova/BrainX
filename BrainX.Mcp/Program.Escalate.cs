using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// Escalation — the one thing the loop is NOT allowed to decide.
//
// Owner (2026-09-19): "ฉันต้องการให้มันทำงานประสานงานกันเอง จนกว่างานลุล่วง
// แล้วรายงานเรา แต่ถ้ามีอะไรต้องให้ฉันตัดสินใจ ต้องเด้งมาในแชท ฝั่งใดฝั่งหนึ่ง".
//
// An autonomous loop is only safe if it has somewhere to STOP. Without this
// file the budget ceilings in Program.Broker.cs are the only brake, and a
// budget stop is a loop giving up silently — which is the same end state as
// the bug this whole thing was built to fix, reached by a more expensive
// route. A decision request is the loop saying "this one is yours", parking
// the work, and going quiet until it is answered.
//
// WHERE A QUESTION GOES. The owner picked all four: Telegram, the Agent Chat
// card, a Windows toast, and the live Claude session. They are not four
// implementations — they are two:
//
//   * A decision FILE under broker/decisions/. BrainX.Client already polls
//     this vault for the dashboard, already owns a window, and is the only
//     part of this system that can raise a toast — so the card and the toast
//     are the same write, rendered by the process that can actually render
//     them. The MCP stays a console app.
//   * A DELIVERY: Telegram over HTTPS, and a bus message into whatever live
//     session exists. A bus message is the only push into a running agent
//     that exists at all — it rides the piggyback onto the session's next
//     tool call, and its Stop hook wakes on it after that.
//
// SECRETS. The Telegram bot token travels in the URL PATH, not a header —
// so every exception message, every log line and every error string from that
// API carries the credential. Learned on NetWix, and the fix is the same one:
// nothing from this file reaches a log without going through Redact().
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    private sealed record BrokerDecision(
        string Id,
        string Agent,
        string Work,
        string Question,
        IReadOnlyList<string> Options);

    private static readonly HttpClient EscalateHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Every secret this process could possibly leak, stripped. Applied to
    /// anything that reaches a log or the console — the Telegram token is in
    /// the URL path, so an unredacted HttpRequestException prints it.
    /// </summary>
    private static string Redact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        s = Regex.Replace(s, @"bot\d{5,}:[A-Za-z0-9_\-]{20,}", "bot<redacted>");
        var tok = Environment.GetEnvironmentVariable("BRAINX_TELEGRAM_TOKEN");
        if (!string.IsNullOrWhiteSpace(tok)) s = s.Replace(tok, "<redacted>");
        if (!string.IsNullOrWhiteSpace(_telegramToken)) s = s.Replace(_telegramToken!, "<redacted>");
        return s;
    }

    private static string? _telegramToken;
    private static string? _telegramChat;

    /// <summary>
    /// True only inside <c>brainx-mcp broker</c>.
    ///
    /// agent_ask_user runs this file's code from INSIDE the MCP server, where
    /// stdout is the JSON-RPC pipe. One stray Console.WriteLine there is a
    /// corrupt frame and a dead session — so the console half of BrokerLog is
    /// gated on the CLI, and the file half always runs.
    /// </summary>
    private static bool _brokerIsCli;

    // ───────────── raising one ─────────────

    /// <summary>
    /// Park the work and ask the owner. Idempotent on <c>Id</c>: a decision
    /// already open is NOT re-sent, because the loop re-evaluates every tick
    /// and a question that notifies once per tick is a question the owner
    /// mutes.
    /// </summary>
    private static async Task EscalateAsync(BrokerConfig cfg, BrokerDecision d)
    {
        try
        {
            Directory.CreateDirectory(BrokerDecisionDir);
            var path = Path.Combine(BrokerDecisionDir, SanitizeAgentSlug(d.Id) + ".json");
            if (File.Exists(path))
            {
                try
                {
                    var existing = JObject.Parse(File.ReadAllText(path));
                    if (string.Equals(existing["status"]?.ToString(), "open", StringComparison.OrdinalIgnoreCase))
                        return;   // already asked; do not ask twice
                }
                catch { /* unreadable decision file: fall through and rewrite it */ }
            }

            // An ANSWERED question must not come straight back.
            //
            // Found by running the broker twice against a seeded vault: the
            // owner answers "reassign the work", the decision closes, and the
            // very next tick sees the same unchanged condition — no runner for
            // that agent — and asks the identical question again. On a 15s
            // poll that is four Telegram messages a minute, forever, and the
            // answer field is no defence because the file is closed by then.
            //
            // Worse, it was feeding itself: delivering the answer puts a
            // message in that agent's inbox, so "1 message waiting" became "2
            // message(s) waiting" and each repeat looked like a NEW, more
            // urgent situation.
            //
            // The stamp the wake hooks already use is exactly the right shape:
            // a condition that is still true in two hours is worth mentioning
            // again, one that resolves in between is never mentioned twice.
            var stampId = "decision-" + SanitizeAgentSlug(d.Id);
            if (WakeIsCoolingDown(stampId)) return;
            StampWake(stampId);

            var rec = new JObject
            {
                ["id"] = d.Id,
                ["agent"] = d.Agent,
                ["work"] = d.Work,
                ["question"] = d.Question,
                ["options"] = JArray.FromObject(d.Options),
                ["status"] = "open",
                ["askedUtc"] = DateTime.UtcNow.ToString("o"),
            };
            AtomicWriteJson(path, rec);
            BrokerLog($"DECISION NEEDED [{d.Id}] {d.Question}");

            // The card and the toast are this file; the client renders both.
            // Telegram and the live session are deliveries, and each is
            // best-effort on its own — losing one channel must not cost the
            // others, or a stale Telegram token silently disables the toast.
            await SendTelegramAsync(cfg, d).ConfigureAwait(false);
            TellLiveSessions(d);
        }
        catch (Exception ex) { BrokerLog("escalate failed — " + Redact(ex.Message)); }
    }

    /// <summary>Is this agent's work parked on the owner right now?</summary>
    private static bool HasOpenDecision(string agent)
    {
        try
        {
            if (!Directory.Exists(BrokerDecisionDir)) return false;
            foreach (var f in Directory.GetFiles(BrokerDecisionDir, "*.json"))
            {
                try
                {
                    var o = JObject.Parse(File.ReadAllText(f));
                    if (!string.Equals(o["status"]?.ToString(), "open", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(o["agent"]?.ToString(), agent, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    // ───────────── answers coming back ─────────────

    /// <summary>
    /// Collect answers and hand them to the agent that is waiting.
    ///
    /// Two ways in, because the owner is not always at this machine: a reply
    /// typed into the dashboard (or any editor) sets <c>answer</c> in the
    /// decision file, and a Telegram button press arrives through getUpdates.
    /// Either way the answer is delivered as a bus message to the agent, which
    /// unparks it on its next tick.
    /// </summary>
    private static async Task PumpDecisionsAsync(BrokerConfig cfg)
    {
        await PollTelegramAsync(cfg).ConfigureAwait(false);

        if (!Directory.Exists(BrokerDecisionDir)) return;
        foreach (var f in Directory.GetFiles(BrokerDecisionDir, "*.json"))
        {
            try
            {
                var o = JObject.Parse(File.ReadAllText(f));
                if (!string.Equals(o["status"]?.ToString(), "open", StringComparison.OrdinalIgnoreCase)) continue;

                var answer = o["answer"]?.ToString();
                if (string.IsNullOrWhiteSpace(answer)) continue;

                var agent = o["agent"]?.ToString() ?? "";
                var question = o["question"]?.ToString() ?? "";
                // Only into a box something can actually open. Delivering an
                // answer to an agent with no runner and no session grows the
                // very queue depth the broker reports back to the owner —
                // the loop feeding its own trigger. The decision still closes;
                // it just does not leave litter behind.
                var deliverable = cfg.Runners.ContainsKey(agent) || IsOnline(PresenceAgeSeconds(agent));
                if (!string.IsNullOrWhiteSpace(agent) && deliverable)
                {
                    DeliverBusMessage("broker", agent,
                        $"The owner answered your question.\n\nQ: {question}\nA: {answer}\n\n"
                        + "Continue the work with that decision. Do not ask it again.",
                        topic: "owner-decision",
                        work: o["work"]?.ToString());
                }

                o["status"] = "answered";
                o["answeredUtc"] = DateTime.UtcNow.ToString("o");
                AtomicWriteJson(f, o);

                // The hop counter is what the budget gate reads. An answered
                // question means the owner wants this to continue, so the
                // ceiling that stopped it must not stop it again on the very
                // next tick.
                var st = ReadRunnerState(agent);
                st.Hops = 0;
                SaveRunnerState(agent, st);

                BrokerLog($"decision [{o["id"]}] answered: {answer}"
                          + (deliverable ? $" — handed to {agent}"
                                         : $" — noted; '{agent}' has no runner and no session, so nothing was queued for it"));
            }
            catch (Exception ex) { BrokerLog("decision pump — " + Redact(ex.Message)); }
        }
    }

    // ───────────── agent_ask_user ─────────────

    /// <summary>
    /// An agent stopping to ask the owner, mid-work.
    ///
    /// The broker can only escalate what IT can see — an agent with no runner,
    /// a budget ceiling. It cannot see that the work itself has reached a fork
    /// only the owner can take, and that is the fork that matters: it is the
    /// difference between a loop that runs until the job is done and a loop
    /// that runs until it does something the owner did not want.
    ///
    /// Returns immediately. The agent is told to stop, not to block — a tool
    /// that waited would hold this brain's single stdio pipe for as long as the
    /// owner takes to answer, which on a phone is hours. The answer arrives as
    /// ordinary inbox mail, which every wake path already handles.
    /// </summary>
    private static JToken AgentAskUser(JObject args)
    {
        var question = args["question"]?.ToString();
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("question is required — say what the owner has to decide, with enough context to answer it without opening anything");

        var options = args["options"]?.ToObject<List<string>>() ?? new List<string>();
        options = options.Where(o => !string.IsNullOrWhiteSpace(o)).Select(o => o.Trim()).Take(4).ToList();

        var me = BusIdentity();
        var work = args["work"]?.ToString() is { Length: > 0 } w ? SanitizeAgentSlug(w) : "";
        var d = new BrokerDecision(
            Id: $"ask-{DateTime.UtcNow.Ticks}-{Guid.NewGuid().ToString("N")[..4]}",
            Agent: me,
            Work: work,
            Question: question!.Trim(),
            Options: options);

        var cfg = LoadBrokerConfig();
        // Fire-and-forget would lose the Telegram send on a short-lived CLI
        // session, and the HTTP call is capped at 20s by EscalateHttp.
        EscalateAsync(cfg, d).GetAwaiter().GetResult();

        return new JObject
        {
            ["asked"] = true,
            ["id"] = d.Id,
            ["agent"] = me,
            ["work"] = work,
            ["channels"] = new JArray(ChannelsUsed(cfg)),
            ["hint"] = "Asked. STOP working this item now — the broker has parked it so nothing else picks it up, "
                     + "and asking again will not reach the owner any faster. Tell your user you are waiting and on what. "
                     + "The answer arrives as a normal message in your inbox (topic 'owner-decision'); your Stop hook "
                     + "wakes you on it, so you do not need to poll."
        };
    }

    private static IEnumerable<string> ChannelsUsed(BrokerConfig cfg)
    {
        yield return "dashboard";
        if (LoadTelegram(cfg)) yield return "telegram";
        var live = KnownAgents().Count(a => IsOnline(PresenceAgeSeconds(a)));
        if (live > 1) yield return $"{live - 1} live peer session(s)";
    }

    // ───────────── channel: the bus (a live session) ─────────────

    /// <summary>
    /// Write a message into an agent's inbox WITHOUT an MCP handshake.
    ///
    /// The broker is a process, not a session, so it cannot call agent_send —
    /// but the mailbox is a directory, and matching the on-disk format exactly
    /// is what makes a broker message indistinguishable from a peer's to every
    /// reader: agent_inbox, the wake hooks, the piggyback counter and the
    /// dashboard all parse these files and none of them need to know.
    /// </summary>
    private static void DeliverBusMessage(string from, string to, string body, string? topic, string? work)
    {
        var box = CollapseToReadableBox(SanitizeAgentSlug(to));
        var inbox = BusInboxDir(box);
        Directory.CreateDirectory(inbox);

        // Same ceiling agent_send enforces. An inbox at its cap means nobody is
        // reading it, and piling broker mail on top of that buries whatever is
        // already there under messages about messages.
        if (Directory.EnumerateFiles(inbox, "*.json").Count() >= MaxInboxPending)
        {
            BrokerLog($"{box}: inbox full ({MaxInboxPending}) — not delivering broker mail");
            return;
        }

        var payload = new JObject
        {
            ["id"] = $"m-{DateTime.UtcNow.Ticks}-{Guid.NewGuid().ToString("N")[..6]}",
            ["ts"] = DateTime.UtcNow.ToString("o"),
            ["from"] = from,
            ["fromClient"] = "brainx-broker",
            ["to"] = box,
            ["body"] = body
        };
        if (!string.IsNullOrWhiteSpace(topic)) payload["topic"] = topic;
        if (!string.IsNullOrWhiteSpace(work)) payload["work"] = work;

        var file = $"{DateTime.UtcNow.Ticks:D19}-{from}-{Guid.NewGuid().ToString("N")[..4]}.json";
        AtomicWriteJson(Path.Combine(inbox, file), payload);
    }

    /// <summary>
    /// Put the question in front of every agent that is actually running, so
    /// the owner sees it in whichever chat window they happen to be looking
    /// at — "เด้งมาในแชท ฝั่งใดฝั่งหนึ่ง", literally.
    ///
    /// Only ONLINE agents, and never the one that asked: mail to an offline
    /// agent would sit until it next starts and then announce a decision that
    /// was probably made hours ago.
    /// </summary>
    private static void TellLiveSessions(BrokerDecision d)
    {
        var opts = d.Options.Count > 0 ? "\nOptions: " + string.Join(" · ", d.Options) : "";
        foreach (var agent in KnownAgents())
        {
            if (agent.Equals(d.Agent, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsOnline(PresenceAgeSeconds(agent))) continue;
            try
            {
                DeliverBusMessage("broker", agent,
                    $"The broker needs the OWNER to decide something before '{d.Agent}' can continue.\n\n"
                    + d.Question + opts
                    + "\n\nShow this to your user and ask. When they answer, tell the broker by writing "
                    + $"the `answer` field into .obsidianx/agent-bus/broker/decisions/{SanitizeAgentSlug(d.Id)}.json — "
                    + "do NOT decide it yourself.",
                    topic: "needs-decision", work: d.Work);
            }
            catch (Exception ex) { BrokerLog($"telling {agent} — " + Redact(ex.Message)); }
        }
    }

    // ───────────── channel: Telegram ─────────────

    private static bool LoadTelegram(BrokerConfig cfg)
    {
        // Env var first: a token in a vault file is a token in whatever backs
        // that vault up. The config field stays for convenience, and being
        // second means setting the env var overrides a stale one on disk.
        _telegramToken = Environment.GetEnvironmentVariable("BRAINX_TELEGRAM_TOKEN");
        if (string.IsNullOrWhiteSpace(_telegramToken))
            _telegramToken = (cfg.Escalation["telegram"] as JObject)?["botToken"]?.ToString();

        _telegramChat = Environment.GetEnvironmentVariable("BRAINX_TELEGRAM_CHAT");
        if (string.IsNullOrWhiteSpace(_telegramChat))
            _telegramChat = (cfg.Escalation["telegram"] as JObject)?["chatId"]?.ToString();

        return !string.IsNullOrWhiteSpace(_telegramToken) && !string.IsNullOrWhiteSpace(_telegramChat);
    }

    private static async Task SendTelegramAsync(BrokerConfig cfg, BrokerDecision d)
    {
        if (!LoadTelegram(cfg)) return;
        try
        {
            // One button per option, each carrying the decision id, so a press
            // can be matched back to its question even after the owner has let
            // several pile up.
            var keyboard = new JArray(d.Options.Select(o => new JArray(new JObject
            {
                ["text"] = o,
                ["callback_data"] = $"{d.Id}|{o}"
            })).ToArray());

            var text = $"🧠 *BrainX* — `{d.Agent}` needs a decision\n\n{EscapeTelegramMarkdown(d.Question)}";
            var body = new JObject
            {
                ["chat_id"] = _telegramChat,
                ["text"] = text,
                ["parse_mode"] = "Markdown",
            };
            if (d.Options.Count > 0)
                body["reply_markup"] = new JObject { ["inline_keyboard"] = keyboard };

            await TelegramCallAsync("sendMessage", body).ConfigureAwait(false);
            BrokerLog($"decision [{d.Id}] sent to Telegram");
        }
        catch (Exception ex) { BrokerLog("telegram send — " + Redact(ex.Message)); }
    }

    /// <summary>
    /// Read button presses. <c>getUpdates</c> is long-poll-free here on
    /// purpose (timeout 0): this runs inside the broker's own tick, and a
    /// 30-second long poll would hold the whole loop — including the spawn
    /// decisions — hostage to Telegram being reachable.
    /// </summary>
    private static async Task PollTelegramAsync(BrokerConfig cfg)
    {
        if (!LoadTelegram(cfg)) return;
        try
        {
            var offsetPath = Path.Combine(BrokerDir, "telegram-offset.txt");
            long offset = 0;
            try { if (File.Exists(offsetPath)) long.TryParse(File.ReadAllText(offsetPath).Trim(), out offset); } catch { }

            var res = await TelegramCallAsync("getUpdates", new JObject
            {
                ["offset"] = offset,
                ["timeout"] = 0,
                ["allowed_updates"] = new JArray("callback_query"),
            }).ConfigureAwait(false);

            if (res?["result"] is not JArray updates || updates.Count == 0) return;

            foreach (var u in updates)
            {
                offset = Math.Max(offset, (u["update_id"]?.ToObject<long?>() ?? 0) + 1);

                var data = u["callback_query"]?["callback_data"]?.ToString()
                        ?? u["callback_query"]?["data"]?.ToString();
                if (string.IsNullOrWhiteSpace(data)) continue;

                var bar = data.IndexOf('|');
                if (bar <= 0) continue;
                var id = SanitizeAgentSlug(data[..bar]);
                var answer = data[(bar + 1)..];

                var path = Path.Combine(BrokerDecisionDir, id + ".json");
                if (!File.Exists(path)) continue;

                var o = JObject.Parse(File.ReadAllText(path));
                // Don't overwrite an answer that already landed another way —
                // the owner may have replied on the dashboard first, and the
                // second answer would re-open a closed decision.
                if (!string.Equals(o["status"]?.ToString(), "open", StringComparison.OrdinalIgnoreCase)) continue;

                o["answer"] = answer;
                o["answeredVia"] = "telegram";
                AtomicWriteJson(path, o);
                BrokerLog($"telegram answer for [{id}]: {answer}");

                var cbId = u["callback_query"]?["id"]?.ToString();
                if (!string.IsNullOrWhiteSpace(cbId))
                    await TelegramCallAsync("answerCallbackQuery", new JObject
                    {
                        ["callback_query_id"] = cbId,
                        ["text"] = "ส่งให้ตัวแทนแล้ว",
                    }).ConfigureAwait(false);
            }

            // Written only after the batch is processed. Advancing the offset
            // first would acknowledge updates whose answers were never stored,
            // and Telegram never sends them again.
            try { File.WriteAllText(offsetPath, offset.ToString(), new UTF8Encoding(false)); } catch { }
        }
        catch (Exception ex) { BrokerLog("telegram poll — " + Redact(ex.Message)); }
    }

    private static async Task<JObject?> TelegramCallAsync(string method, JObject body)
    {
        var url = $"https://api.telegram.org/bot{_telegramToken}/{method}";
        using var content = new StringContent(body.ToString(), new UTF8Encoding(false), "application/json");
        using var resp = await EscalateHttp.PostAsync(url, content).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            BrokerLog($"telegram {method} → {(int)resp.StatusCode} {Redact(text)}");
            return null;
        }
        try { return JObject.Parse(text); } catch { return null; }
    }

    /// <summary>
    /// Telegram's Markdown, not the table escaper of the same idea elsewhere in
    /// this type — that one caps at 180 characters and strips newlines, which
    /// would silently truncate the owner's question and run its options
    /// together. Different job, different name, on purpose.
    /// </summary>
    private static string EscapeTelegramMarkdown(string s) =>
        s.Replace("_", "\\_").Replace("*", "\\*").Replace("`", "\\`").Replace("[", "\\[");
}
