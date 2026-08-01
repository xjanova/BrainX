using System.Globalization;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Converts a piece of text into a token count.
///
/// This replaces the <c>chars / 4</c> rule that used to sit inline in three
/// places. That rule is an English-prose approximation, and this vault is
/// mostly Thai — measured against Claude's own reported <c>output_tokens</c>
/// over 624 all-text assistant messages, <c>chars/4</c> had a MEDIAN ABSOLUTE
/// ERROR OF 67.6%. It understates Thai by about 4x, which is the direction
/// that flatters the brain: every cost the brain incurs looked a quarter of
/// its real size.
///
/// The model here is deliberately the simplest thing that fits:
///
///     tokens ≈ A·(ascii chars) + B·(non-ascii chars) + C
///
/// Two script buckets and a per-message constant. Fitted by ordinary least
/// squares on HALF the samples and scored on the other half — a coefficient
/// set that has only ever seen the data it was fitted to is not evidence of
/// anything. On that held-out half the median absolute error is 6.8%.
///
/// GROUND TRUTH. The training data is not a survey or a guess: Claude Code's
/// own transcripts record, for every assistant message, both the text and the
/// token count Anthropic billed for it. Messages containing `thinking` blocks
/// are excluded — those tokens are billed but their text is not written to
/// the transcript, so including them would attribute invisible tokens to
/// visible characters. Messages with tool_use blocks are excluded for the
/// same reason (the serialised tool input is not the text we can see).
/// </summary>
public sealed class TokenEstimator
{
    /// <summary>Tokens per ASCII character (≈ 4.9 chars/token).</summary>
    public double AsciiTokensPerChar { get; private set; } = DefaultAscii;

    /// <summary>Tokens per non-ASCII character (Thai ≈ 0.84 chars/token).</summary>
    public double NonAsciiTokensPerChar { get; private set; } = DefaultNonAscii;

    /// <summary>Fixed per-message overhead (role markers, block framing).</summary>
    public double MessageOverhead { get; private set; } = DefaultOverhead;

    /// <summary>Where the current coefficients came from — shown in the UI so
    /// a number is never presented without its provenance.</summary>
    public string Provenance { get; private set; } = DefaultProvenance;

    /// <summary>Samples behind the current coefficients (0 = built-in defaults).</summary>
    public int SampleCount { get; private set; }

    /// <summary>Median absolute error on held-out samples, as a fraction.
    /// Null when running on the built-in defaults' published figure.</summary>
    public double? HeldOutMedianError { get; private set; }

    // Fitted 2026-08-01 over 816 transcripts / 36,108 assistant messages,
    // 624 of them usable as ground truth. Baked in so a fresh install is
    // accurate before any calibration run happens.
    private const double DefaultAscii     = 0.2056;   // 4.86 chars/token
    private const double DefaultNonAscii  = 1.1972;   // 0.84 chars/token
    private const double DefaultOverhead  = 58.2;
    private const string DefaultProvenance = "built-in fit · n=624 · held-out median error 6.8%";

    public TokenEstimator() { }

    /// <summary>
    /// Estimate the tokens in a single message's worth of text.
    /// Pass <paramref name="includeOverhead"/> false when summing many
    /// fragments that belong to one message, so the constant is not
    /// charged per fragment.
    /// </summary>
    public long Estimate(string? text, bool includeOverhead = true)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var (ascii, nonAscii) = CountScripts(text);
        var t = ascii * AsciiTokensPerChar
              + nonAscii * NonAsciiTokensPerChar
              + (includeOverhead ? MessageOverhead : 0);
        return t <= 0 ? 0 : (long)Math.Round(t);
    }

    /// <summary>
    /// Estimate from character counts alone, for callers that only recorded a
    /// length (the tool log stores <c>resp_size</c>, not the payload). Without
    /// the text we cannot know the script mix, so the caller must say what it
    /// assumes — there is no honest default. <paramref name="nonAsciiFraction"/>
    /// of 0 means pure ASCII, 1 means pure Thai.
    /// </summary>
    public long EstimateFromLength(long chars, double nonAsciiFraction, bool includeOverhead = true)
    {
        if (chars <= 0) return 0;
        nonAsciiFraction = Math.Clamp(nonAsciiFraction, 0, 1);
        var nonAscii = chars * nonAsciiFraction;
        var ascii = chars - nonAscii;
        var t = ascii * AsciiTokensPerChar
              + nonAscii * NonAsciiTokensPerChar
              + (includeOverhead ? MessageOverhead : 0);
        return t <= 0 ? 0 : (long)Math.Round(t);
    }

    /// <summary>ASCII vs non-ASCII character counts. Thai, CJK and the rest
    /// all tokenize far denser than Latin; splitting them further did not
    /// improve held-out error enough to justify the extra parameter.</summary>
    public static (long Ascii, long NonAscii) CountScripts(ReadOnlySpan<char> text)
    {
        long ascii = 0, nonAscii = 0;
        foreach (var ch in text)
        {
            if (ch < 0x80) ascii++;
            else nonAscii++;
        }
        return (ascii, nonAscii);
    }

    // ---------------------------------------------------------------- calibration

    private static string CalibrationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrainX", "token-calibration.json");

    /// <summary>Load previously fitted coefficients, if any. Silently keeps the
    /// built-in defaults when the file is missing, unreadable, or implausible —
    /// a corrupt calibration must never be able to make the numbers worse than
    /// the defaults they replaced.</summary>
    public bool TryLoadCalibration()
    {
        try
        {
            if (!File.Exists(CalibrationPath)) return false;
            var o = JObject.Parse(File.ReadAllText(CalibrationPath));
            var a = o["asciiTokensPerChar"]?.ToObject<double?>() ?? 0;
            var b = o["nonAsciiTokensPerChar"]?.ToObject<double?>() ?? 0;
            var c = o["messageOverhead"]?.ToObject<double?>() ?? 0;
            var n = o["sampleCount"]?.ToObject<int?>() ?? 0;
            var err = o["heldOutMedianError"]?.ToObject<double?>();

            // Sanity envelope. A tokenizer that needs >5 tokens per character,
            // or fewer than one per 20, is a fit that went wrong.
            if (a <= 0.005 || a > 5 || b <= 0.005 || b > 5 || c < -500 || c > 5000 || n < MinSamples)
                return false;

            AsciiTokensPerChar = a;
            NonAsciiTokensPerChar = b;
            MessageOverhead = c;
            SampleCount = n;
            HeldOutMedianError = err;
            var when = o["fittedAt"]?.ToObject<DateTime?>();
            Provenance = err is double e
                ? $"calibrated{(when is DateTime w ? " " + w.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "")}" +
                  $" · n={n} · held-out median error " +
                  (e * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%"
                : $"calibrated · n={n}";
            return true;
        }
        catch { return false; }
    }

    /// <summary>Minimum usable samples before a fit is allowed to replace the
    /// defaults. Below this the coefficients are noise wearing a decimal point.</summary>
    public const int MinSamples = 60;

    public sealed record CalibrationResult(
        double Ascii, double NonAscii, double Overhead,
        int SampleCount, double HeldOutMedianError, double HeldOutP90Error,
        double BaselineMedianError, string Note);

    /// <summary>
    /// Re-fit the coefficients from Claude's own transcripts and persist them.
    /// Returns null when there was not enough ground truth to beat the
    /// defaults honestly.
    /// </summary>
    public CalibrationResult? Calibrate(string? projectsRoot = null, int maxFiles = 400, CancellationToken ct = default)
    {
        var samples = ClaudeTranscriptReader.CollectTextOnlySamples(projectsRoot, maxFiles, ct);
        if (samples.Count < MinSamples) return null;

        // Split alternately rather than randomly: deterministic, so two runs on
        // the same transcripts give the same answer and a regression in the fit
        // is visible instead of being lost in sampling noise.
        var train = new List<(long ascii, long nonAscii, long tokens)>();
        var test = new List<(long ascii, long nonAscii, long tokens)>();
        for (int i = 0; i < samples.Count; i++)
            (i % 2 == 0 ? train : test).Add(samples[i]);

        var fitTrain = Solve(train);
        if (fitTrain is null) return null;

        double MedianErr(List<(long ascii, long nonAscii, long tokens)> set, Func<long, long, double> predict)
        {
            var errs = set.Select(s => Math.Abs(predict(s.ascii, s.nonAscii) - s.tokens) / (double)s.tokens)
                          .OrderBy(x => x).ToList();
            return errs.Count == 0 ? double.NaN : errs[errs.Count / 2];
        }
        double P90Err(List<(long ascii, long nonAscii, long tokens)> set, Func<long, long, double> predict)
        {
            var errs = set.Select(s => Math.Abs(predict(s.ascii, s.nonAscii) - s.tokens) / (double)s.tokens)
                          .OrderBy(x => x).ToList();
            return errs.Count == 0 ? double.NaN : errs[(int)(errs.Count * 0.9)];
        }

        var (ta, tb, tc) = fitTrain.Value;
        var held = MedianErr(test, (a, n) => ta * a + tb * n + tc);
        var heldP90 = P90Err(test, (a, n) => ta * a + tb * n + tc);
        var baseline = MedianErr(test, (a, n) => (a + n) / 4.0);

        // Refuse to install a fit that is worse than the rule it replaces.
        // "We calibrated it" is not a reason to trust a number; beating the
        // baseline on data the fit never saw is.
        if (double.IsNaN(held) || held >= baseline) return null;

        var fitAll = Solve(samples) ?? fitTrain.Value;
        AsciiTokensPerChar = fitAll.Item1;
        NonAsciiTokensPerChar = fitAll.Item2;
        MessageOverhead = fitAll.Item3;
        SampleCount = samples.Count;
        HeldOutMedianError = held;
        // InvariantCulture, or this machine's Thai locale renders the year as
        // 2569 (Buddhist era) in a string that is shown to the user and written
        // to a file other tools parse. The same trap already produced a
        // "2569" garden report once.
        Provenance = $"calibrated {DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} " +
                     $"· n={samples.Count} · held-out median error " +
                     (held * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CalibrationPath)!);
            var o = new JObject
            {
                ["asciiTokensPerChar"] = AsciiTokensPerChar,
                ["nonAsciiTokensPerChar"] = NonAsciiTokensPerChar,
                ["messageOverhead"] = MessageOverhead,
                ["sampleCount"] = SampleCount,
                ["heldOutMedianError"] = held,
                ["heldOutP90Error"] = heldP90,
                ["baselineMedianError"] = baseline,
                ["fittedAt"] = DateTime.UtcNow,
            };
            // No BOM: this file is read by non-.NET tooling too, and a BOM
            // breaks JSON.parse in Node.
            File.WriteAllText(CalibrationPath, o.ToString(), new System.Text.UTF8Encoding(false));
        }
        catch { /* the fit still applies in-process even if it cannot be saved */ }

        return new CalibrationResult(AsciiTokensPerChar, NonAsciiTokensPerChar, MessageOverhead,
            SampleCount, held, heldP90, baseline,
            $"beat chars/4 on held-out data ({held:P1} vs {baseline:P1})");
    }

    /// <summary>Ordinary least squares for <c>y = a·x1 + b·x2 + c</c> via the
    /// 3x3 normal equations. Returns null if the system is singular (which
    /// happens when every sample is the same script).</summary>
    private static (double, double, double)? Solve(List<(long ascii, long nonAscii, long tokens)> set)
    {
        double n = 0, Sa = 0, Sb = 0, Sy = 0, Saa = 0, Sbb = 0, Sab = 0, Say = 0, Sby = 0;
        foreach (var (a, b, y) in set)
        {
            double x1 = a, x2 = b, yy = y;
            n++; Sa += x1; Sb += x2; Sy += yy;
            Saa += x1 * x1; Sbb += x2 * x2; Sab += x1 * x2;
            Say += x1 * yy; Sby += x2 * yy;
        }
        var m = new[,] { { Saa, Sab, Sa }, { Sab, Sbb, Sb }, { Sa, Sb, n } };
        var v = new[] { Say, Sby, Sy };
        var det = Det3(m);
        if (Math.Abs(det) < 1e-6) return null;

        var res = new double[3];
        for (int k = 0; k < 3; k++)
        {
            var mk = (double[,])m.Clone();
            for (int r = 0; r < 3; r++) mk[r, k] = v[r];
            res[k] = Det3(mk) / det;
        }
        // Negative tokens-per-char is physically meaningless; reject rather
        // than ship a fit that predicts negative cost for long messages.
        if (res[0] <= 0 || res[1] <= 0) return null;
        return (res[0], res[1], res[2]);
    }

    private static double Det3(double[,] m) =>
          m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1])
        - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0])
        + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
}
