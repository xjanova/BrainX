using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// What the brain actually costs, measured.
///
/// This replaces TokenSavingsTracker, which reported a "net saved" figure
/// built from a table of invented per-op constants (search = 5,000 tokens
/// avoided, create_note = 7,500, …). Those numbers were never measurements.
/// The class documented them honestly in its own summary — "All numbers are
/// deliberate over-estimates … the brain has no way to know what Claude WOULD
/// have done without it" — and then the UI printed the result as a live
/// counter with a plus sign in front of it.
///
/// Nothing here estimates a counterfactual. The brain's cost is knowable and
/// is therefore the number reported: how many calls it served and how many
/// tokens those responses put in front of the model. What the brain SAVED
/// requires observing sessions that ran without it, which is
/// <see cref="TokenUsageAggregator.ModeComparison"/>'s job, and which returns
/// null until such sessions exist.
/// </summary>
public class BrainCostTracker
{
    private readonly TokenEstimator _estimator;

    public BrainCostTracker(TokenEstimator? estimator = null)
    {
        _estimator = estimator ?? new TokenEstimator();
        _estimator.TryLoadCalibration();
    }

    /// <summary>See <see cref="TokenUsageAggregator"/> — same assumption, same
    /// reason, applied only to rows logged before response composition was
    /// recorded.</summary>
    private const double LegacyNonAsciiAssumption = 0.45;

    public class Stats
    {
        public int TotalCalls { get; set; }
        /// <summary>MEASURED. Tokens the brain's responses added to context.</summary>
        public long ResponseTokens { get; set; }
        public long ResponseChars { get; set; }
        public Dictionary<string, int> CallsByOp { get; set; } = new();
        /// <summary>False when any counted row predated composition logging.</summary>
        public bool FullyMeasured { get; set; } = true;
        /// <summary>How the token figure was derived.</summary>
        public string Provenance { get; set; } = "";
    }

    /// <summary>
    /// Measure over a window. <paramref name="hoursBack"/> of 0 or less means
    /// everything the log still holds.
    /// </summary>
    public Stats Compute(int hoursBack = 0)
    {
        var s = new Stats { Provenance = _estimator.Provenance };
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "tool-log.ndjson");
        if (!File.Exists(path)) return s;

        var since = hoursBack > 0 ? DateTime.UtcNow.AddHours(-hoursBack) : DateTime.MinValue;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JObject obj;
                try { obj = JObject.Parse(line); } catch { continue; }

                var tool = obj["tool"]?.ToString();
                // Suffix match, not prefix: the ObsidianX → BrainX rename made
                // three prefix-matching call sites measure zero for 67 days.
                if (tool == null || !tool.Contains("__brain_", StringComparison.OrdinalIgnoreCase)) continue;

                if (since > DateTime.MinValue)
                {
                    var raw = obj["ts"]?.ToString();
                    if (string.IsNullOrEmpty(raw)) continue;
                    if (!DateTime.TryParse(raw, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var ts)) continue;
                    if (ts.ToUniversalTime() < since) continue;
                }

                var chars = obj["resp_size"]?.ToObject<long?>() ?? 0;
                var nonAscii = obj["resp_nonascii"]?.ToObject<long?>();
                long tokens;
                if (nonAscii is long na && na >= 0 && na <= chars)
                    tokens = _estimator.EstimateFromLength(chars, chars > 0 ? na / (double)chars : 0);
                else
                {
                    tokens = _estimator.EstimateFromLength(chars, LegacyNonAsciiAssumption);
                    if (chars > 0) s.FullyMeasured = false;
                }

                s.TotalCalls++;
                s.ResponseChars += chars;
                s.ResponseTokens += tokens;

                var op = ShortOpName(tool);
                s.CallsByOp[op] = s.CallsByOp.GetValueOrDefault(op) + 1;
            }
        }
        catch { /* best-effort: a truncated tail stops the count, doesn't crash */ }
        return s;
    }

    /// <summary>"mcp__brainx-brain__brain_search" → "brain_search".</summary>
    private static string ShortOpName(string tool)
    {
        var idx = tool.LastIndexOf("__", StringComparison.Ordinal);
        return idx >= 0 && idx + 2 < tool.Length ? tool[(idx + 2)..] : tool;
    }
}
