using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

// ─────────────────────────────────────────────────────────────────────────
// The two long polls wait off the request loop.
//
// cowork_read and agent_inbox take wait_seconds: hold the call open until a
// line or a letter arrives. They waited with Thread.Sleep inside Handle, on
// the loop that reads this client's next request — so for up to ten seconds
// every other call from the same client (brain_search, cowork_task list, the
// next cowork_say) queued behind the wait. CluadeX's room relay reads with
// wait_seconds:10 in a loop while the owner waits for an answer, which kept
// its whole brain pipe busy for the length of the wait (audit 2026-10-19).
//
// Now the loop hands such a request to a task and reads on. The task waits —
// polling the folder, outside the request gate — until there is something or
// the time is up, then answers the call as an ordinary one with nothing left
// to wait for, under the gate like every other request. Responses carry the
// request's id, and the clients of this server match them by id (Claude Code,
// Codex, Gemini CLI, CluadeX's McpStdioTransport); the launcher in front of
// this worker passes lines through and tracks what is in flight by id too.
// ─────────────────────────────────────────────────────────────────────────

internal static partial class Program
{
    /// <summary>One response line at a time on stdout: the loop and the long
    /// polls both write.</summary>
    private static readonly SemaphoreSlim _stdoutGate = new(1, 1);

    internal sealed record LongPoll(JObject Request, string Tool, int Seconds);

    /// <summary>Waits served off the loop at once. Each polls the folder twice
    /// a second; a client firing dozens would turn that into a disk loop. Past
    /// this many, a wait is served the old way, inline — bounded at ten seconds.</summary>
    private const int MaxLongPolls = 8;
    private static int _longPolls;

    /// <summary>Hand the wait to its own task, if there is room for one more.
    /// False = serve it inline.</summary>
    private static bool TryServeLongPoll(LongPoll poll, TextWriter writer)
    {
        if (Interlocked.Increment(ref _longPolls) > MaxLongPolls)
        {
            Interlocked.Decrement(ref _longPolls);
            Log($"long poll: {MaxLongPolls} waits already open — serving {poll.Tool} inline");
            return false;
        }
        _ = Task.Run(async () =>
        {
            try { await ServeLongPollAsync(poll, writer).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _longPolls); }
        });
        return true;
    }

    /// <summary>
    /// A tools/call that asks to wait — cowork_read or agent_inbox with
    /// wait_seconds above zero — with the wait clamped the way the tool clamps
    /// it. Null for everything else, which is served in order on the loop.
    /// </summary>
    internal static LongPoll? LongPollOf(string line)
    {
        // Cheap first: most lines are not this, and need not be parsed twice.
        if (!line.Contains("wait_seconds", StringComparison.Ordinal)) return null;
        JObject req;
        try { req = JObject.Parse(line.TrimStart('﻿')); }
        catch { return null; }
        if (req["method"]?.ToString() != "tools/call" || req["id"] is null or { Type: JTokenType.Null }) return null;
        if (req["params"] is not JObject p || p["arguments"] is not JObject args) return null;
        var tool = p["name"]?.ToString();
        var max = tool switch { "cowork_read" => CoworkMaxWaitSeconds, "agent_inbox" => MaxWaitSeconds, _ => 0 };
        if (max == 0) return null;
        int wait;
        try { wait = args["wait_seconds"]?.ToObject<int>() ?? 0; }
        catch { return null; }   // the tool reports a bad value itself
        wait = Math.Clamp(wait, 0, max);
        return wait > 0 ? new LongPoll(req, tool!, wait) : null;
    }

    /// <summary>
    /// Wait until the room or the inbox has something for this session, or the
    /// time is up — without holding the request gate — then serve the call with
    /// its wait already spent, and write the answer.
    /// </summary>
    private static async Task ServeLongPollAsync(LongPoll poll, TextWriter writer)
    {
        string response;
        try
        {
            string me;
            lock (_requestGate) me = BusIdentity();
            var deadline = DateTime.UtcNow.AddSeconds(poll.Seconds);
            var step = poll.Tool == "agent_inbox" ? 750 : 500;
            while (DateTime.UtcNow < deadline && !LongPollReady(poll.Tool, me))
                await Task.Delay(step).ConfigureAwait(false);

            ((JObject)poll.Request["params"]!["arguments"]!)["wait_seconds"] = 0;
            var line = poll.Request.ToString(Formatting.None);
            lock (_requestGate) response = Handle(line);
        }
        catch (Exception ex)
        {
            Log($"long poll error: {ex}");
            response = BuildError(poll.Request["id"], -32603, $"internal error: {ex.Message}");
        }
        try { await WriteResponseAsync(writer, response).ConfigureAwait(false); }
        catch (Exception ex) { Log($"long poll answer not written (client gone?): {ex.Message}"); }
    }

    /// <summary>What the tool's own wait loop checks: unread room lines past
    /// this session's cursor (CoworkRead), mail in this agent's box
    /// (AgentInbox). Read-only — files only, no shared state.</summary>
    private static bool LongPollReady(string tool, string me)
    {
        try
        {
            if (tool == "agent_inbox")
            {
                var inbox = BusInboxDir(me);
                return Directory.Exists(inbox) && Directory.EnumerateFiles(inbox, "*.json").Any();
            }
            var member = ReadJsonOrNull(CoworkMemberFile(me));
            var (cursor, seen) = member != null ? CoworkMyCursor(member) : ("", null);
            return CoworkAfter(cursor, seen).Any(f => CoworkIsNews(f, member));
        }
        catch { return false; }
    }

    private static async Task WriteResponseAsync(TextWriter writer, string response)
    {
        if (string.IsNullOrEmpty(response)) return;
        await _stdoutGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(response).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        finally { _stdoutGate.Release(); }
    }
}
