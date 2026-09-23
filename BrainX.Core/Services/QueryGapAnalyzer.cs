using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Active learning loop — reads <c>.obsidianx/access-log.ndjson</c> and
/// surfaces queries the user keeps searching for but the brain doesn't
/// answer well.
///
/// Two signals indicate a "gap" in the brain:
///   1. <b>Sparse results</b> — search returned few matches (the brain
///      doesn't have the topic).
///   2. <b>Low follow-through</b> — search wasn't followed by a
///      <c>get_note</c> within a few minutes (results weren't useful).
///
/// Either signal alone is noisy — combined with a repeat-count threshold
/// they filter to "topics worth writing a note about". Pure read; this
/// service writes nothing.
/// </summary>
public class QueryGapAnalyzer
{
    /// <summary>How long after a search a get_note still counts as "follow-through".</summary>
    public TimeSpan FollowThroughWindow { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Group search log entries within this window into a single search call.
    /// (One brain_search call writes one log line per result, all with the same ts ±ms.)</summary>
    public TimeSpan SearchCallWindow { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Minimum repeat count before a query is suggested. Below this is noise.</summary>
    public int MinSearchCount { get; set; } = 2;

    /// <summary>Average result count below which the brain is considered to lack the topic.</summary>
    public double SparseResultThreshold { get; set; } = 3.0;

    /// <summary>Follow-through rate below which results are considered unhelpful.</summary>
    public double LowFollowThroughThreshold { get; set; } = 0.3;

    public class Suggestion
    {
        public string Query { get; set; } = "";
        public int SearchCount { get; set; }
        public double AvgResults { get; set; }
        public double FollowThroughRate { get; set; }
        public DateTime LastSearched { get; set; }
        public string Reason { get; set; } = "";
    }

    public class Report
    {
        public int WindowDays { get; set; }
        public int TotalSearches { get; set; }
        public int UniqueQueries { get; set; }
        public List<Suggestion> Suggestions { get; set; } = [];
    }

    public Report Analyze(string vaultPath, int windowDays = 14, int limit = 10)
    {
        var since = DateTime.UtcNow.AddDays(-windowDays);
        var logPath = Path.Combine(vaultPath, ".obsidianx", "access-log.ndjson");

        var report = new Report { WindowDays = windowDays };
        if (!File.Exists(logPath)) return report;

        // Pass 1 — parse all entries in window into typed events.
        var events = new List<LogEvent>();
        foreach (var line in ReadLinesSafe(logPath))
        {
            if (!TryParse(line, out var ev) || ev.Ts < since) continue;
            events.Add(ev);
        }
        if (events.Count == 0) return report;

        events.Sort((a, b) => a.Ts.CompareTo(b.Ts));

        // Pass 2a — search calls as the MCP now records them: one "query" row
        // per call, with the exact number of notes it returned — zero
        // included. A search that found nothing used to write no row at all,
        // so the clearest gap there is ("asked, and the brain had nothing")
        // was the one this analyzer could never see.
        var searchCalls = new List<SearchCall>();
        var recorded = new List<SearchCall>();
        foreach (var ev in events.Where(e => e.Op == "query"))
        {
            var call = new SearchCall
            {
                QueryKey = ev.Context.Trim().ToLowerInvariant(),
                QueryDisplay = ev.Context.Trim(),
                StartTs = ev.Ts,
                LastTs = ev.Ts,
                ResultCount = Math.Max(0, ev.Hits ?? 0)
            };
            recorded.Add(call);
            searchCalls.Add(call);
        }

        // Pass 2b — history from before "query" rows existed: rebuild calls
        // from the per-result rows (one brain_search call emits N lines, one
        // per result, same context, same ms-ish), skipping any call a query
        // row already describes. These can only ever count calls that FOUND
        // something, which is why 2a exists.
        SearchCall? cur = null;
        foreach (var ev in events.Where(e => e.Op == "search" || e.Op == "semantic_search"))
        {
            var key = ev.Context.Trim().ToLowerInvariant();
            if (cur != null
                && cur.QueryKey == key
                && (ev.Ts - cur.LastTs) <= SearchCallWindow)
            {
                cur.ResultCount++;
                cur.LastTs = ev.Ts;
            }
            else
            {
                cur = new SearchCall
                {
                    QueryKey = key,
                    QueryDisplay = ev.Context.Trim(),
                    StartTs = ev.Ts,
                    LastTs = ev.Ts,
                    ResultCount = 1
                };
                var start = cur.StartTs;
                if (!recorded.Any(r => r.QueryKey == key && (r.StartTs - start).Duration() <= SearchCallWindow))
                    searchCalls.Add(cur);
            }
        }

        // Pass 3 — for each search call, did a get_note happen within FollowThroughWindow?
        var getNotes = events.Where(e => e.Op == "get_note").ToList();
        foreach (var sc in searchCalls)
        {
            sc.HadFollowThrough = getNotes.Any(g =>
                g.Ts >= sc.StartTs
                && g.Ts <= sc.LastTs.Add(FollowThroughWindow));
        }

        // Pass 4 — group calls by query, compute stats.
        var byQuery = searchCalls
            .GroupBy(c => c.QueryKey)
            .Select(g => new
            {
                Key = g.Key,
                Display = g.First().QueryDisplay,
                Count = g.Count(),
                AvgResults = g.Average(c => (double)c.ResultCount),
                FollowThrough = g.Count(c => c.HadFollowThrough) / (double)g.Count(),
                LastTs = g.Max(c => c.LastTs)
            })
            .ToList();

        report.TotalSearches = searchCalls.Count;
        report.UniqueQueries = byQuery.Count;

        // Pass 5 — filter to suggestions and rank.
        var suggestions = byQuery
            .Where(q => q.Count >= MinSearchCount)
            .Where(q => q.AvgResults < SparseResultThreshold
                     || q.FollowThrough < LowFollowThroughThreshold)
            .Where(q => !string.IsNullOrEmpty(q.Display))
            .Select(q => new Suggestion
            {
                Query = q.Display,
                SearchCount = q.Count,
                AvgResults = Math.Round(q.AvgResults, 2),
                FollowThroughRate = Math.Round(q.FollowThrough, 2),
                LastSearched = q.LastTs,
                Reason = BuildReason(q.Count, q.AvgResults, q.FollowThrough)
            })
            // Rank: more searches first, then sparser results, then lower follow-through.
            .OrderByDescending(s => s.SearchCount)
            .ThenBy(s => s.AvgResults)
            .ThenBy(s => s.FollowThroughRate)
            .Take(limit)
            .ToList();

        report.Suggestions = suggestions;
        return report;
    }

    private static string BuildReason(int count, double avg, double followThrough)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var bits = new List<string> { $"{count} searches" };
        // Nothing found: follow-through is meaningless when there was nothing
        // to follow through to, so it is not reported as a second failure.
        if (avg <= 0.0) return string.Join(", ", bits) + ", found nothing every time — the brain does not have this";
        bits.Add(string.Format(inv, "avg {0:F1} hits", avg));
        if (followThrough <= 0.0) bits.Add("never followed up");
        else if (followThrough < 0.3) bits.Add(string.Format(inv, "only {0:F0}% led to a read", followThrough * 100));
        return string.Join(", ", bits) + " — looks like a brain gap";
    }

    private class LogEvent
    {
        public DateTime Ts { get; init; }
        public string Op { get; init; } = "";
        public string Context { get; init; } = "";
        public string NodeId { get; init; } = "";
        /// <summary>Notes a "query" row's search returned; null on every other row.</summary>
        public int? Hits { get; init; }
    }

    private class SearchCall
    {
        public string QueryKey { get; set; } = "";
        public string QueryDisplay { get; set; } = "";
        public DateTime StartTs { get; set; }
        public DateTime LastTs { get; set; }
        public int ResultCount { get; set; }
        public bool HadFollowThrough { get; set; }
    }

    private static bool TryParse(string line, out LogEvent ev)
    {
        ev = null!;
        try
        {
            var obj = JObject.Parse(line);
            var raw = obj["ts"]?.ToString();
            if (string.IsNullOrEmpty(raw)) return false;
            ev = new LogEvent
            {
                Ts = DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime(),
                Op = obj["op"]?.ToString() ?? "",
                Context = obj["context"]?.ToString() ?? "",
                NodeId = obj["node_id"]?.ToString() ?? "",
                Hits = obj["hits"]?.Type == JTokenType.Integer ? obj["hits"]!.Value<int>() : null
            };
            return true;
        }
        catch { return false; }
    }

    private static IEnumerable<string> ReadLinesSafe(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!string.IsNullOrWhiteSpace(line)) yield return line;
        }
    }
}
