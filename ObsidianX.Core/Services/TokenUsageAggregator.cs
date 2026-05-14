using Newtonsoft.Json.Linq;

namespace ObsidianX.Core.Services;

/// <summary>
/// Builds the data behind the Token Economy comparison chart by joining
/// two log streams:
///
///   - <c>.obsidianx/access-log.ndjson</c> — every brain MCP call
///     (<c>brain_search</c>, <c>get_note</c>, etc.). Authoritative for
///     the "savings" side of the comparison.
///   - <c>~/.claude/tool-log.ndjson</c> — every Claude Code tool call
///     (Read, Edit, Bash, Grep, …). Written by the PostToolUse `.*`
///     hook. Authoritative for "what Claude actually did this turn".
///
/// We do NOT have access to actual prompt/response token counts —
/// those live inside the Anthropic API. So both lines on the chart
/// are HEURISTIC ESTIMATES, deliberately conservative on the "savings"
/// side so the gauge doesn't lie about value.
/// </summary>
public class TokenUsageAggregator
{
    private readonly TokenSavingsTracker _savings = new();

    /// <summary>Per-tool average token cost. Tuned from spot
    /// observation of typical Read/Grep/Edit fan-outs in Claude Code.</summary>
    public Dictionary<string, int> ToolCosts { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Read"]      = 800,
        ["Edit"]      = 600,
        ["MultiEdit"] = 1200,
        ["Write"]     = 1500,
        ["Bash"]      = 700,
        ["PowerShell"] = 700,
        ["Grep"]      = 500,
        ["Glob"]      = 200,
        ["WebSearch"] = 2500,
        ["WebFetch"]  = 3500,
        ["Agent"]     = 5000,
        ["TodoWrite"] = 200,
    };

    public class HourBucket
    {
        /// <summary>Bucket start time (UTC). Width = <see cref="Series.BucketMinutes"/>.</summary>
        public DateTime Hour { get; set; }
        /// <summary>Tokens actually spent in this bucket (brain calls + tool calls).</summary>
        public long ActualSpent { get; set; }
        /// <summary>Estimated tokens that brain calls saved (would have been
        /// extra Read/Grep work without the brain).</summary>
        public long BrainSaved { get; set; }
        /// <summary>Projection: what the same activity would have cost
        /// without the brain. = ActualSpent + BrainSaved.</summary>
        public long ProjectionWithoutBrain => ActualSpent + BrainSaved;
        public int BrainCalls { get; set; }
        public int OtherToolCalls { get; set; }
        /// <summary>Dominant brain-mode this bucket ("always"/"auto"/"off"/"mixed").</summary>
        public string DominantMode { get; set; } = "unknown";
    }

    public class Series
    {
        public List<HourBucket> Buckets { get; set; } = [];
        /// <summary>Width of each bucket in minutes. Used by the renderer to
        /// label the X axis and to decide tick density.</summary>
        public int BucketMinutes { get; set; } = 60;
        /// <summary>Earliest timestamp the aggregator was asked to consider
        /// (UTC). May be earlier than the first non-empty bucket.</summary>
        public DateTime RangeStart { get; set; }
        /// <summary>Now (UTC) at the moment Compute was called.</summary>
        public DateTime RangeEnd { get; set; }
        public long TotalActual => Buckets.Sum(b => b.ActualSpent);
        public long TotalProjection => Buckets.Sum(b => b.ProjectionWithoutBrain);
        public long TotalSaved => Buckets.Sum(b => b.BrainSaved);
        public double SavingsPercent => TotalProjection == 0 ? 0
            : (double)TotalSaved / TotalProjection * 100.0;
    }

    public Series Compute(string vaultPath, int hoursBack = 24 * 14, int bucketMinutes = 60)
    {
        if (bucketMinutes <= 0) bucketMinutes = 60;
        var now = DateTime.UtcNow;
        var since = now.AddHours(-hoursBack);
        var byHour = new Dictionary<DateTime, HourBucket>();

        // Brain calls — saved + spent attribution comes from TokenSavingsTracker.
        var brainLog = Path.Combine(vaultPath, ".obsidianx", "access-log.ndjson");
        if (File.Exists(brainLog))
        {
            foreach (var line in ReadLinesSafe(brainLog))
            {
                if (!TryParseTs(line, out var ts, out var obj)) continue;
                if (ts < since) continue;
                var hour = RoundToBucket(ts, bucketMinutes);
                var bucket = GetOrCreate(byHour, hour);
                var op = obj["op"]?.ToString() ?? "";
                bucket.BrainCalls++;
                bucket.ActualSpent += _savings.CallCost;
                bucket.BrainSaved += _savings.Estimate(op);
            }
        }

        // Tool calls — every Read/Edit/Bash etc. logged by the
        // PostToolUse `.*` hook. Resides in the user profile, not the
        // vault, so multi-vault workflows still aggregate cleanly.
        var toolLog = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "tool-log.ndjson");
        var modeCounts = new Dictionary<DateTime, Dictionary<string, int>>();
        if (File.Exists(toolLog))
        {
            foreach (var line in ReadLinesSafe(toolLog))
            {
                if (!TryParseTs(line, out var ts, out var obj)) continue;
                if (ts < since) continue;
                var hour = RoundToBucket(ts, bucketMinutes);
                var bucket = GetOrCreate(byHour, hour);
                var tool = obj["tool"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(tool)) continue;
                bucket.OtherToolCalls++;
                bucket.ActualSpent += ToolCosts.TryGetValue(tool, out var c) ? c : 600;
                var mode = obj["mode"]?.ToString() ?? "unknown";
                if (!modeCounts.TryGetValue(hour, out var mc))
                    modeCounts[hour] = mc = new Dictionary<string, int>();
                mc[mode] = mc.GetValueOrDefault(mode) + 1;
            }
        }

        // Decide each hour's dominant mode for colouring the chart line:
        // hours where always-mode dominated get the green tint, off-mode
        // gets amber, mixed/auto stays neutral.
        foreach (var (hour, bucket) in byHour)
        {
            if (modeCounts.TryGetValue(hour, out var mc) && mc.Count > 0)
            {
                var top = mc.OrderByDescending(kv => kv.Value).First();
                bucket.DominantMode = mc.Count > 1 && mc.Values.Distinct().Count() > 1
                    ? "mixed" : top.Key;
            }
        }

        return new Series
        {
            Buckets = byHour.Values.OrderBy(b => b.Hour).ToList(),
            BucketMinutes = bucketMinutes,
            RangeStart = since,
            RangeEnd = now,
        };
    }

    private static DateTime RoundToBucket(DateTime ts, int bucketMinutes)
    {
        var ticksPerBucket = TimeSpan.FromMinutes(bucketMinutes).Ticks;
        var bucketTicks = (ts.Ticks / ticksPerBucket) * ticksPerBucket;
        return new DateTime(bucketTicks, DateTimeKind.Utc);
    }

    private static HourBucket GetOrCreate(Dictionary<DateTime, HourBucket> map, DateTime hour)
    {
        if (!map.TryGetValue(hour, out var b))
        {
            b = new HourBucket { Hour = hour };
            map[hour] = b;
        }
        return b;
    }

    private static bool TryParseTs(string line, out DateTime ts, out JObject obj)
    {
        ts = default; obj = new JObject();
        try
        {
            obj = JObject.Parse(line);
            var raw = obj["ts"]?.ToString();
            if (string.IsNullOrEmpty(raw)) return false;
            ts = DateTime.Parse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
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
