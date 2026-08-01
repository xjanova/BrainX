using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Builds the Token Economy page's data. Every number it produces is either a
/// measurement or is labelled as not being one — there is no third category.
///
/// WHAT CHANGED AND WHY. This class used to build its "actual spend" line by
/// multiplying each logged tool call by a hardcoded per-tool constant
/// (Read=800, Grep=500, …), and then drew a second line defined as
/// <c>ActualSpent + BrainSaved</c> and labelled it "without the brain". That
/// second line was an arithmetic identity: it sat above the first by
/// construction, for any input, so the shaded band between them was
/// guaranteed before a single row was read. A chart that cannot fail is not
/// evidence, and no amount of tuning the constants fixes it. It is gone.
///
/// The constants were also simply wrong, measured against the response sizes
/// the very same log records: Read 1.3x low, Grep 1.4x low, PowerShell 6.6x
/// HIGH, brain calls 1.8x low. Errors in both directions do not cancel — they
/// distort the shape of the line, not just its scale.
///
/// So the spend line now comes from Claude's own transcripts, which record
/// what Anthropic actually billed, deduplicated per message
/// (see <see cref="ClaudeTranscriptReader"/>). The tool log is kept for
/// ATTRIBUTION only — which tools were called, and how much text the brain's
/// own tools returned.
/// </summary>
public class TokenUsageAggregator
{
    private readonly TokenEstimator _estimator;

    public TokenUsageAggregator(TokenEstimator? estimator = null)
    {
        _estimator = estimator ?? new TokenEstimator();
        _estimator.TryLoadCalibration();
    }

    public TokenEstimator Estimator => _estimator;

    /// <summary>
    /// Tools whose PostToolUse payload is NOT what the model saw.
    ///
    /// Claude Code's <c>tool_response</c> for a file edit carries
    /// <c>originalFile</c> and the full structured patch — measured average
    /// 319,741 characters — while the model receives a short confirmation.
    /// Their recorded <c>resp_size</c> is a faithful measurement of the wrong
    /// thing, so it is excluded from every token figure and shown as a call
    /// count only. Anything that "just uses resp_size everywhere" imports a
    /// 100x error through this door.
    /// </summary>
    private static readonly HashSet<string> PayloadNotContext =
        new(StringComparer.OrdinalIgnoreCase) { "Edit", "MultiEdit", "Write", "NotebookEdit" };

    /// <summary>
    /// Assumed non-ASCII share for tool-log rows written before the logger
    /// started recording the real figure. Only affects historical rows, and
    /// <see cref="Series.BrainTokensFullyMeasured"/> reports whether any row
    /// needed it.
    /// </summary>
    private const double LegacyNonAsciiAssumption = 0.45;

    public class HourBucket
    {
        /// <summary>Bucket start time (UTC). Width = <see cref="Series.BucketMinutes"/>.</summary>
        public DateTime Hour { get; set; }

        /// <summary>MEASURED. Tokens Anthropic billed in this bucket, summed
        /// across all four classes, deduplicated by message id.</summary>
        public long ActualRaw { get; set; }

        /// <summary>MEASURED. The same spend expressed in input-equivalent
        /// tokens (see <see cref="TokenWeights"/>). This is the one that is
        /// comparable to a quota percentage; the raw sum is not.</summary>
        public double ActualWeighted { get; set; }

        /// <summary>MEASURED. Assistant messages billed in this bucket.</summary>
        public int Messages { get; set; }

        /// <summary>MEASURED. Input + cache-write only: material entering the
        /// context for the first time.
        ///
        /// This is the denominator the brain's share belongs over. A brain
        /// response is billed once as new context and then re-read from cache
        /// on every later turn of the conversation, so measuring it against the
        /// raw total — which is ~90% cache reads of everything ever said —
        /// divides a one-time cost by a running one and reports 0.00% for
        /// something real.</summary>
        public long ActualNewContext { get; set; }

        /// <summary>MEASURED. Tokens the brain's own MCP tools returned into
        /// the context, from recorded response sizes through the calibrated
        /// estimator. This is the brain's cost — its share of the line above,
        /// not a separate line.</summary>
        public long BrainResponseTokens { get; set; }

        public int BrainCalls { get; set; }
        public int OtherToolCalls { get; set; }

        /// <summary>Dominant brain-mode this bucket ("always"/"auto"/"off"/"mixed").</summary>
        public string DominantMode { get; set; } = "unknown";
    }

    /// <summary>Per-tool call counts and, where the payload is the context,
    /// the tokens those responses put in front of the model.</summary>
    public sealed class ToolAttribution
    {
        public string Tool { get; set; } = "";
        public int Calls { get; set; }
        public long ResponseChars { get; set; }
        public long ResponseTokens { get; set; }
        /// <summary>False for Edit/Write and friends — count only, no token figure.</summary>
        public bool TokensMeaningful { get; set; } = true;
    }

    /// <summary>
    /// An observational comparison of token cost per assistant message between
    /// brain modes. NOT a controlled experiment: the modes were not assigned
    /// randomly, so a difference here is a correlation over whatever work
    /// happened to be done in each mode. It is reported with its sample sizes
    /// so it can be read for what it is.
    /// </summary>
    public sealed record ModeComparison(
        string ModeA, double WeightedPerMessageA, int BucketsA, int MessagesA,
        string ModeB, double WeightedPerMessageB, int BucketsB, int MessagesB)
    {
        public double DifferencePercent => WeightedPerMessageB <= 0
            ? 0 : (WeightedPerMessageB - WeightedPerMessageA) / WeightedPerMessageB * 100.0;
    }

    public class Series
    {
        public List<HourBucket> Buckets { get; set; } = [];
        public List<ToolAttribution> Tools { get; set; } = [];
        public int BucketMinutes { get; set; } = 60;
        public DateTime RangeStart { get; set; }
        public DateTime RangeEnd { get; set; }

        public long TotalActualRaw => Buckets.Sum(b => b.ActualRaw);
        public double TotalActualWeighted => Buckets.Sum(b => b.ActualWeighted);
        public long TotalNewContext => Buckets.Sum(b => b.ActualNewContext);
        public long TotalBrainTokens => Buckets.Sum(b => b.BrainResponseTokens);
        public int TotalMessages => Buckets.Sum(b => b.Messages);

        /// <summary>The brain's share of material entering the context.
        /// Replaces the old "savings percent", which measured nothing.
        /// Denominator is new context, not the raw total — see
        /// <see cref="HourBucket.ActualNewContext"/>.</summary>
        public double BrainSharePercent => TotalNewContext == 0
            ? 0 : (double)TotalBrainTokens / TotalNewContext * 100.0;

        /// <summary>False when any tool-log row predates response-composition
        /// logging and had to fall back to an assumed script mix.</summary>
        public bool BrainTokensFullyMeasured { get; set; } = true;

        /// <summary>Null unless two different modes were actually observed.
        /// Null is the honest answer for a system that has only ever run in
        /// one mode, and must render as "no comparison yet".</summary>
        public ModeComparison? Comparison { get; set; }

        /// <summary>How the token figures were derived, for display.</summary>
        public string EstimatorProvenance { get; set; } = "";
    }

    /// <summary>
    /// MEASURED. Characters the brain's own MCP tools returned, from the
    /// recorded <c>resp_size</c>.
    ///
    /// Matched by tool-name SUFFIX. Three sites in this codebase named the
    /// pre-rename server ('mcp__obsidianx-brain__…') and silently measured
    /// zero for 67 days; the prefix is what a rebrand changes, the tool name
    /// is what identifies the call.
    /// </summary>
    public long MeasuredBrainResponseChars(int hoursBack)
    {
        long total = 0;
        foreach (var (obj, _) in ReadToolLog(DateTime.UtcNow.AddHours(-Math.Abs(hoursBack))))
        {
            var tool = obj["tool"]?.ToString();
            if (tool == null || !tool.Contains("__brain_", StringComparison.OrdinalIgnoreCase)) continue;
            total += obj["resp_size"]?.ToObject<long?>() ?? 0;
        }
        return total;
    }

    /// <summary>MEASURED. The same responses converted to tokens through the
    /// calibrated estimator rather than a chars/4 rule that is 67% wrong on
    /// this vault's language mix.</summary>
    public long MeasuredBrainResponseTokens(int hoursBack)
    {
        long total = 0;
        foreach (var (obj, _) in ReadToolLog(DateTime.UtcNow.AddHours(-Math.Abs(hoursBack))))
        {
            var tool = obj["tool"]?.ToString();
            if (tool == null || !tool.Contains("__brain_", StringComparison.OrdinalIgnoreCase)) continue;
            total += TokensForRow(obj, out _);
        }
        return total;
    }

    public Series Compute(string vaultPath, int hoursBack = 24 * 14, int bucketMinutes = 60)
    {
        if (bucketMinutes <= 0) bucketMinutes = 60;
        var now = DateTime.UtcNow;
        var since = now.AddHours(-Math.Abs(hoursBack));
        var byHour = new Dictionary<DateTime, HourBucket>();
        var series = new Series
        {
            BucketMinutes = bucketMinutes,
            RangeStart = since,
            RangeEnd = now,
            EstimatorProvenance = _estimator.Provenance,
        };

        // ---- 1. The spend line. Ground truth: what Anthropic billed. --------
        foreach (var m in ClaudeTranscriptReader.ReadSince(since))
        {
            var bucket = GetOrCreate(byHour, RoundToBucket(m.TimestampUtc, bucketMinutes));
            bucket.ActualRaw += m.Raw;
            bucket.ActualWeighted += m.Weighted;
            bucket.ActualNewContext += m.Input + m.CacheWrite;
            bucket.Messages++;
        }

        // ---- 2. Attribution. Which tools ran, and what the brain returned. --
        var modeCounts = new Dictionary<DateTime, Dictionary<string, int>>();
        var tools = new Dictionary<string, ToolAttribution>(StringComparer.OrdinalIgnoreCase);
        bool fullyMeasured = true;

        foreach (var (obj, ts) in ReadToolLog(since))
        {
            var tool = obj["tool"]?.ToString();
            if (string.IsNullOrEmpty(tool)) continue;

            var hour = RoundToBucket(ts, bucketMinutes);
            var bucket = GetOrCreate(byHour, hour);
            var isBrain = tool.Contains("__brain_", StringComparison.OrdinalIgnoreCase);
            if (isBrain) bucket.BrainCalls++; else bucket.OtherToolCalls++;

            var chars = obj["resp_size"]?.ToObject<long?>() ?? 0;
            var meaningful = !PayloadNotContext.Contains(tool);
            long tokens = 0;
            if (meaningful)
            {
                tokens = TokensForRow(obj, out var measuredMix);
                if (!measuredMix) fullyMeasured = false;
                if (isBrain) bucket.BrainResponseTokens += tokens;
            }

            if (!tools.TryGetValue(tool, out var att))
                tools[tool] = att = new ToolAttribution { Tool = tool, TokensMeaningful = meaningful };
            att.Calls++;
            if (meaningful)
            {
                att.ResponseChars += chars;
                att.ResponseTokens += tokens;
            }

            var mode = obj["mode"]?.ToString() ?? "unknown";
            if (!modeCounts.TryGetValue(hour, out var mc))
                modeCounts[hour] = mc = new Dictionary<string, int>();
            mc[mode] = mc.GetValueOrDefault(mode) + 1;
        }

        foreach (var (hour, bucket) in byHour)
        {
            if (modeCounts.TryGetValue(hour, out var mc) && mc.Count > 0)
            {
                var top = mc.OrderByDescending(kv => kv.Value).First();
                bucket.DominantMode = mc.Count > 1 && mc.Values.Distinct().Count() > 1
                    ? "mixed" : top.Key;
            }
        }

        series.Buckets = byHour.Values.OrderBy(b => b.Hour).ToList();
        series.Tools = tools.Values.OrderByDescending(t => t.Calls).ToList();
        series.BrainTokensFullyMeasured = fullyMeasured;
        series.Comparison = CompareModes(series.Buckets);
        return series;
    }

    /// <summary>
    /// Compare cost per assistant message between the two most-represented
    /// modes. Returns null unless two distinct modes each have enough buckets
    /// to be worth comparing — the system has run in a single mode for its
    /// whole life so far, and inventing a comparison from that would be the
    /// same mistake the projection line made.
    /// </summary>
    private static ModeComparison? CompareModes(List<HourBucket> buckets)
    {
        const int MinBuckets = 5;
        var groups = buckets
            .Where(b => b.Messages > 0 && b.DominantMode is not ("unknown" or "mixed"))
            .GroupBy(b => b.DominantMode)
            .Select(g => new
            {
                Mode = g.Key,
                Buckets = g.Count(),
                Messages = g.Sum(b => b.Messages),
                Weighted = g.Sum(b => b.ActualWeighted),
            })
            .Where(g => g.Buckets >= MinBuckets && g.Messages > 0)
            .OrderByDescending(g => g.Messages)
            .ToList();

        if (groups.Count < 2) return null;
        var a = groups[0];
        var b = groups[1];
        return new ModeComparison(
            a.Mode, a.Weighted / a.Messages, a.Buckets, a.Messages,
            b.Mode, b.Weighted / b.Messages, b.Buckets, b.Messages);
    }

    /// <summary>Tokens for one tool-log row. Uses the real non-ASCII count when
    /// the logger recorded it, otherwise falls back to an assumed script mix
    /// and reports that it did so.</summary>
    private long TokensForRow(JObject obj, out bool measuredMix)
    {
        var chars = obj["resp_size"]?.ToObject<long?>() ?? 0;
        if (chars <= 0) { measuredMix = true; return 0; }

        var nonAscii = obj["resp_nonascii"]?.ToObject<long?>();
        if (nonAscii is long na && na >= 0 && na <= chars)
        {
            measuredMix = true;
            return _estimator.EstimateFromLength(chars, na / (double)chars);
        }
        measuredMix = false;
        return _estimator.EstimateFromLength(chars, LegacyNonAsciiAssumption);
    }

    private static IEnumerable<(JObject Obj, DateTime Ts)> ReadToolLog(DateTime since)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "tool-log.ndjson");
        if (!File.Exists(path)) yield break;

        IEnumerator<string> lines;
        try { lines = ReadLinesSafe(path).GetEnumerator(); }
        catch (IOException) { yield break; }

        using (lines)
        {
            while (true)
            {
                try { if (!lines.MoveNext()) break; }
                catch (IOException) { break; }
                if (!TryParseTs(lines.Current, out var ts, out var obj)) continue;
                if (ts < since) continue;
                yield return (obj, ts);
            }
        }
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
