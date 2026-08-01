using System.Globalization;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Answers "how many tokens is 100% of the quota?" by measuring it, because
/// there is nothing to look up.
///
/// Anthropic publishes plan limits in MESSAGES per 5 hours and states that the
/// figure varies with conversation length, attachments and model. There is no
/// published token number, so hardcoding one found elsewhere would be
/// inventing the answer and then reporting it to four significant figures.
///
/// What we can do is watch the meter. Two observations of
/// (usage percent, cumulative weighted tokens) taken close together give
///
///     quota ≈ 100 · Δtokens / Δpercent
///
/// CUMULATIVE, NOT WINDOWED. The obvious version of this — correlate the
/// rolling 5 h token tally against the 5 h percentage — is wrong, because the
/// two windows are not the same window: ours slides continuously, theirs
/// resets on session boundaries, so Δ(our window) also contains whatever aged
/// out of the back of it. Differencing an all-time cumulative counter removes
/// that entirely: between two samples, Δcumulative is exactly the tokens spent
/// in between, whatever either window was doing.
///
/// WEIGHTED, NOT RAW. See <see cref="TokenWeights"/> — cache reads are 90% of
/// the raw count and bill at a tenth. Dividing a raw sum by a percentage
/// produces a precise-looking number that is off by roughly 10x.
///
/// Every guard here exists to make the estimate refusable. A pair that spans a
/// quota reset, a shrinking cumulative, or a percentage that barely moved is
/// discarded rather than averaged in, and below <see cref="MinPairs"/> usable
/// pairs the answer is "not enough data yet" — not a number with a wide error
/// bar that everyone will read as a number.
/// </summary>
public sealed class QuotaCalibrator
{
    /// <summary>Usable pairs required before an estimate is reported at all.</summary>
    public const int MinPairs = 5;

    /// <summary>Smallest percentage movement worth differencing. The reported
    /// percentage is coarse; below this, quantization dominates the estimate.</summary>
    public const double MinPercentDelta = 1.0;

    /// <summary>Longest gap between two samples that may be paired. Over this,
    /// a reset could have happened and been undone before we looked again.</summary>
    public static readonly TimeSpan MaxPairGap = TimeSpan.FromMinutes(60);

    private static string SamplesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrainX", "quota-samples.ndjson");

    public readonly record struct Sample(
        DateTime AtUtc, double Percent, double CumulativeWeighted, long CumulativeRaw, string Plan);

    public sealed record Estimate(
        double TokensPer100Percent,
        double IqrLow,
        double IqrHigh,
        int PairCount,
        string Plan,
        DateTime NewestSampleUtc)
    {
        /// <summary>Half the IQR width as a fraction of the median — a plain
        /// "how much do these pairs disagree" number for the UI.</summary>
        public double Spread => TokensPer100Percent <= 0
            ? 1.0 : (IqrHigh - IqrLow) / 2.0 / TokensPer100Percent;
    }

    /// <summary>Newest sample this process wrote, so the duplicate check does
    /// not re-read and re-parse the whole file. Record is called from the UI
    /// thread on every probe tick; ReadAll there would put a growing file walk
    /// in front of the user's frame.</summary>
    private Sample? _lastWritten;

    /// <summary>Keep the file bounded. Only pairs within
    /// <see cref="MaxPairGap"/> contribute, so ancient samples add nothing but
    /// parse time.</summary>
    private const int MaxSamplesRetained = 5000;

    /// <summary>
    /// Append one observation. A sample whose percent and cumulative both
    /// match the previous one adds nothing and is dropped, so a UI that polls
    /// every few seconds does not fill the file with duplicates.
    /// </summary>
    public void Record(double percent, double cumulativeWeighted, long cumulativeRaw, string? plan)
    {
        if (percent < 0 || percent > 100) return;          // probe reports -1 when unauthenticated
        if (cumulativeWeighted <= 0) return;

        try
        {
            // First write of the process: read once to find where we left off,
            // then never again.
            if (_lastWritten is null)
            {
                var existing = ReadAll();
                _lastWritten = existing.Count > 0 ? existing[^1] : default(Sample);
            }

            if (_lastWritten is { } last && last.AtUtc != default
                && Math.Abs(last.Percent - percent) < 0.01
                && Math.Abs(last.CumulativeWeighted - cumulativeWeighted) < 1)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(SamplesPath)!);
            var o = new JObject
            {
                ["ts"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["percent"] = percent,
                ["cumWeighted"] = cumulativeWeighted,
                ["cumRaw"] = cumulativeRaw,
                ["plan"] = plan ?? "unknown",
            };
            File.AppendAllText(SamplesPath, o.ToString(Newtonsoft.Json.Formatting.None) + "\n",
                new System.Text.UTF8Encoding(false));
            _lastWritten = new Sample(DateTime.UtcNow, percent, cumulativeWeighted, cumulativeRaw, plan ?? "unknown");
            _writesSinceTrim++;
            if (_writesSinceTrim >= 500) { _writesSinceTrim = 0; TrimIfHuge(); }
        }
        catch { /* telemetry must never break the page it decorates */ }
    }

    private int _writesSinceTrim;

    private void TrimIfHuge()
    {
        try
        {
            var all = ReadAll();
            if (all.Count <= MaxSamplesRetained) return;
            var keep = all.Skip(all.Count - MaxSamplesRetained);
            var tmp = SamplesPath + ".tmp";
            using (var w = new StreamWriter(tmp, false, new System.Text.UTF8Encoding(false)))
            {
                foreach (var s in keep)
                {
                    var o = new JObject
                    {
                        ["ts"] = s.AtUtc.ToString("o", CultureInfo.InvariantCulture),
                        ["percent"] = s.Percent,
                        ["cumWeighted"] = s.CumulativeWeighted,
                        ["cumRaw"] = s.CumulativeRaw,
                        ["plan"] = s.Plan,
                    };
                    w.WriteLine(o.ToString(Newtonsoft.Json.Formatting.None));
                }
            }
            File.Move(tmp, SamplesPath, overwrite: true);
        }
        catch { }
    }

    public List<Sample> ReadAll()
    {
        var list = new List<Sample>();
        try
        {
            if (!File.Exists(SamplesPath)) return list;
            foreach (var line in File.ReadLines(SamplesPath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var o = JObject.Parse(line);
                    var ts = o["ts"]?.ToObject<DateTime?>();
                    var pct = o["percent"]?.ToObject<double?>();
                    var cw = o["cumWeighted"]?.ToObject<double?>();
                    if (ts is null || pct is null || cw is null) continue;
                    list.Add(new Sample(
                        ts.Value.ToUniversalTime(), pct.Value, cw.Value,
                        o["cumRaw"]?.ToObject<long?>() ?? 0,
                        o["plan"]?.ToString() ?? "unknown"));
                }
                catch { }
            }
        }
        catch (IOException) { }
        return list;
    }

    // Compute runs on every chart render, including canvas resize. Re-parsing
    // thousands of lines per frame is a stall the user would feel, and the
    // answer cannot change faster than a probe tick anyway.
    private Estimate? _cached;
    private DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The estimate, or null when the samples do not support one. Null is a
    /// real answer here and the UI must render it as "not enough data",
    /// never as 0.
    /// </summary>
    public Estimate? Compute()
    {
        if (DateTime.UtcNow - _cachedAt < CacheTtl) return _cached;
        _cachedAt = DateTime.UtcNow;
        _cached = ComputeUncached();
        return _cached;
    }

    private Estimate? ComputeUncached()
    {
        var samples = ReadAll().OrderBy(s => s.AtUtc).ToList();
        if (samples.Count < 2) return null;

        var perPair = new List<double>();
        for (int i = 1; i < samples.Count; i++)
        {
            var a = samples[i - 1];
            var b = samples[i];

            var gap = b.AtUtc - a.AtUtc;
            if (gap <= TimeSpan.Zero || gap > MaxPairGap) continue;

            var dPct = b.Percent - a.Percent;
            // Percent fell: the 5 h session rolled over between samples, so the
            // tokens spent in this interval are not what moved the meter.
            if (dPct < MinPercentDelta) continue;

            var dTok = b.CumulativeWeighted - a.CumulativeWeighted;
            // Cumulative fell: transcripts were pruned or a different machine's
            // history appeared. Either way the difference is meaningless.
            if (dTok <= 0) continue;

            perPair.Add(100.0 * dTok / dPct);
        }

        if (perPair.Count < MinPairs) return null;

        perPair.Sort();
        double Q(double p)
        {
            var idx = (int)Math.Clamp(Math.Round(p * (perPair.Count - 1)), 0, perPair.Count - 1);
            return perPair[idx];
        }

        // Median, not mean: one pair that straddled an unnoticed reset would
        // drag a mean anywhere it liked.
        return new Estimate(
            TokensPer100Percent: Q(0.5),
            IqrLow: Q(0.25),
            IqrHigh: Q(0.75),
            PairCount: perPair.Count,
            Plan: samples[^1].Plan,
            NewestSampleUtc: samples[^1].AtUtc);
    }

    /// <summary>How far off an estimate is from being reportable, for the UI's
    /// "collecting…" state. Returns null once <see cref="Compute"/> succeeds.</summary>
    public string? ProgressNote()
    {
        if (Compute() is not null) return null;
        var samples = ReadAll();
        if (samples.Count == 0) return "no usage samples yet — open the Claude usage card while working";
        var usable = 0;
        var ordered = samples.OrderBy(s => s.AtUtc).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            var gap = ordered[i].AtUtc - ordered[i - 1].AtUtc;
            if (gap > TimeSpan.Zero && gap <= MaxPairGap
                && ordered[i].Percent - ordered[i - 1].Percent >= MinPercentDelta
                && ordered[i].CumulativeWeighted > ordered[i - 1].CumulativeWeighted)
                usable++;
        }
        return $"{usable}/{MinPairs} usable sample pairs — needs the meter to move while BrainX is watching";
    }
}
