using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using BrainX.Core.Services.Chrome;
using BrainX.Mcp.Bridge;

namespace BrainX.Mcp;

/// <summary>
/// private_browse_search — the owner's search over what Claude in Chrome saw.
///
/// LOCAL STDIO ONLY. The remote /mcp child (BRAINX_HEADLESS=1) refuses it here,
/// does not list it (ToolsList), and McpRemotePolicy hard-blocks the name on
/// top — three independent locks on the one tool that reads private browsing.
///
/// Like the chrome__* calls themselves, a search leaves no trace in the vault:
/// the query can be as private as the pages ("the bank page with my balance"),
/// so it is not journaled, not put in the activity feed, and not used to
/// deliver bus notices.
/// </summary>
internal static partial class Program
{
    private const int PrivateSearchMaxLimit = 50;

    /// <summary>
    /// A run nobody is watching: started by the broker for a queued task, or
    /// the Mind on the local model. Such a run takes its orders from a task
    /// card or a peer — a prompt-injected or peer-instructed agent there could
    /// dump the owner's private log and forward it with agent_send or cowork,
    /// which sync. The owner searches their browsing; an unattended run never.
    /// </summary>
    private static bool IsUnattendedRun =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BRAINX_AS_AGENT"))
        || Environment.GetEnvironmentVariable("BRAINX_BROKER_RUN") == "1";

    /// <summary>
    /// Is there private browsing an owner could search here? The bridge is on,
    /// or a log already exists. Local, attended sessions only.
    /// </summary>
    private static bool PrivateSearchVisible()
    {
        if (IsRemoteChild || IsUnattendedRun) return false;
        try
        {
            if (ChromeBridge.EnabledNow) return true;
            return ChromeBridge.Store is { } s && File.Exists(s.DbPath);
        }
        catch { return false; }
    }

    /// <summary>Can this session see private browsing at all — chrome__ tools
    /// or the search? Decides whether the instructions carry the privacy rule.</summary>
    private static bool PrivateBrowsingVisible()
    {
        if (IsRemoteChild) return false;
        try { return ChromeBridge.EnabledNow || PrivateSearchVisible(); }
        catch { return false; }
    }

    private static string PrivateBrowseSearchCall(JToken? id, JObject args)
    {
        if (IsRemoteChild)
            return PrivateError(id,
                "private_browse_search is local-only — it is never available over the remote endpoint " +
                "(ค้นประวัติการท่องเว็บส่วนตัวได้เฉพาะในเครื่องนี้เท่านั้น)");

        if (IsUnattendedRun)
            return PrivateError(id,
                "private_browse_search is for the owner only — it is refused in unattended runs (broker tasks, Mind) " +
                "(ค้นประวัติการท่องเว็บส่วนตัวได้เฉพาะเจ้าของ — งานที่รันเองโดยไม่มีคนดูใช้ไม่ได้)");

        var store = ChromeBridge.Store;
        if (store == null)
            return PrivateError(id,
                "the private browsing log is unavailable: its folder would be inside the vault, so nothing is kept " +
                "(ไม่มีบันทึกส่วนตัว — โฟลเดอร์ที่ตั้งไว้อยู่ในคลังสมอง จึงไม่เก็บอะไรเลย)");

        // The log is written behind the calls; the page the owner just read
        // should be findable now, so wait (briefly) for the writer.
        try { ChromeBridge.FlushPrivateLog(TimeSpan.FromSeconds(15)); } catch { }

        try
        {
            // One screenshot, as a real image block.
            var shotId = args["screenshot_id"]?.Type == JTokenType.String ? args["screenshot_id"]!.ToString().Trim() : null;
            if (!string.IsNullOrEmpty(shotId))
            {
                var bytes = store.ReadShot(shotId.ToLowerInvariant(), out var mime);
                if (bytes == null)
                    return PrivateError(id,
                        "no such screenshot (it may have been cleared or aged out) " +
                        "(ไม่พบภาพหน้าจอนี้ — อาจถูกล้างหรือหมดอายุแล้ว)");
                return BuildResult(id, new JObject
                {
                    ["content"] = new JArray
                    {
                        new JObject { ["type"] = "text", ["text"] = $"screenshot {shotId} from the private browsing log" },
                        new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(bytes), ["mimeType"] = mime },
                    },
                });
            }

            var query = args["query"]?.Type == JTokenType.String ? args["query"]!.ToString() : "";
            if (query.Length > 500) query = query[..500];
            var limit = args["limit"]?.Type == JTokenType.Integer ? args["limit"]!.Value<int>() : 10;
            limit = Math.Clamp(limit, 1, PrivateSearchMaxLimit);
            int? days = args["days"]?.Type == JTokenType.Integer ? args["days"]!.Value<int>() : null;

            var hits = store.Search(query, limit, days);
            var results = new JArray();
            foreach (var h in hits)
                results.Add(new JObject
                {
                    ["id"] = h.Id,
                    ["ts"] = h.TsUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    ["agent"] = h.Agent,
                    ["tool"] = ChromeBridgeIds.Prefix + h.Tool,
                    ["url"] = h.Url,
                    ["snippet"] = h.Snippet,
                    ["isError"] = h.IsError,
                    ["screenshots"] = new JArray(h.Shots.Select(s => s.Id)),
                });

            var body = new JObject
            {
                ["query"] = query,
                ["count"] = hits.Count,
                ["results"] = results,
                ["retentionDays"] = store.RetentionDays,
                ["note"] = "Private browsing log on this PC only — never in the brain. " + ChromeBridgeIds.PrivacyRule,
            };
            return BuildResult(id, new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = body.ToString(Formatting.Indented) } },
            });
        }
        catch (Exception ex)
        {
            // The type, never the message: an SQLite error can echo the query.
            return PrivateError(id,
                $"the private browsing log could not be read ({ex.GetType().Name}) " +
                "(อ่านบันทึกการท่องเว็บส่วนตัวไม่ได้)");
        }
    }

    private static string PrivateError(JToken? id, string text) => BuildResult(id, new JObject
    {
        ["isError"] = true,
        ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = "Error: " + text } },
    });
}
