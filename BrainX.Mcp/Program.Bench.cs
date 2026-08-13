using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

/// <summary>
/// `brainx-mcp bench` — the measurement the stats banner has asked for since
/// it was written: **does the brain make a weaker model better, and by how
/// much?**
///
/// Everything else in this repo measures RETRIEVAL (does the right note come
/// back). This measures the thing that is actually being sold: a model
/// answering project questions correctly, with the brain and without it, on
/// the same questions, in the same process, back to back.
///
/// The question set (`.obsidianx/eval/facts.json`) is deliberately made of
/// facts that exist ONLY in this vault — a version number chosen last week, a
/// threshold measured on Tuesday. No model can know them from pretraining. So
/// the brain-off arm has exactly two honest moves: say it does not know, or
/// invent something. That split is the headline number here, not accuracy:
///
///   correct   — the reply contains the fact
///   abstained — the reply says it does not know (HONEST, not a failure)
///   wrong     — neither: the model made something up
///
/// A tool that turns `wrong` into `abstained` is worth something even if it
/// never turns one into `correct`, and no accuracy-only benchmark can see
/// that.
/// </summary>
internal static partial class Program
{
    private static readonly string[] IdkMarkers =
    {
        "i don't know", "i do not know", "don't have", "do not have", "no information",
        "not sure", "cannot determine", "can't determine", "unable to", "not aware",
        "no access", "unknown", "not specified", "not mentioned", "insufficient",
        "ไม่ทราบ", "ไม่รู้", "ไม่มีข้อมูล", "ไม่แน่ใจ", "ไม่ได้ระบุ", "ไม่พบ", "บอกไม่ได้"
    };

    internal static async Task<int> BenchCliAsync(string[] args)
    {
        string? vaultArg = null, factsArg = null;
        var models = new List<string>();
        var limit = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vault" && i + 1 < args.Length) vaultArg = args[++i];
            else if (args[i] == "--facts" && i + 1 < args.Length) factsArg = args[++i];
            else if (args[i] == "--models" && i + 1 < args.Length)
                models.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries));
            else if (args[i] == "--limit" && i + 1 < args.Length) int.TryParse(args[++i], out limit);
            else if (args[i] is "-h" or "--help" or "help")
            {
                Console.WriteLine("Usage: brainx-mcp bench [--vault PATH] [--models a,b] [--facts PATH] [--limit N]");
                Console.WriteLine();
                Console.WriteLine("Asks each model the same vault-only questions twice: once cold");
                Console.WriteLine("(brain-off) and once with brain_recall's evidence in context");
                Console.WriteLine("(brain-on). Reports correct / abstained / WRONG -- wrong being a");
                Console.WriteLine("hallucination, since no model can know these facts unaided.");
                return 0;
            }
        }
        if (!string.IsNullOrWhiteSpace(vaultArg) && Directory.Exists(vaultArg))
            _vaultPath = Path.GetFullPath(vaultArg);
        if (models.Count == 0) models.Add("gemma3:4b");

        var factsPath = factsArg ?? Path.Combine(_vaultPath, ".obsidianx", "eval", "facts.json");
        if (!File.Exists(factsPath)) { Console.Error.WriteLine($"no fact set at {factsPath}"); return 2; }
        var facts = (JObject.Parse(File.ReadAllText(factsPath))["facts"] as JArray ?? new JArray())
            .OfType<JObject>().ToList();
        if (limit > 0 && facts.Count > limit) facts = facts.Take(limit).ToList();
        if (facts.Count == 0) { Console.Error.WriteLine("fact set is empty"); return 2; }

        Console.WriteLine($"brainx-mcp bench · v{ServerVersion}");
        Console.WriteLine($"  vault:  {_vaultPath}");
        Console.WriteLine($"  facts:  {Path.GetFileName(factsPath)} · {facts.Count} question(s)");
        Console.WriteLine($"  models: {string.Join(", ", models)}");
        Console.WriteLine();

        var rows = new List<JObject>();
        foreach (var model in models)
        {
            // Three arms, because the middle one is not the product. "on" is
            // notes-in-context with no regard for how sure retrieval was;
            // "on+verdict" tells the model what brain_recall's verdict was and
            // what it means. If the calibration shipped this morning is worth
            // anything, the difference between those two arms is where it
            // shows up -- as hallucinations turning back into "I don't know".
            foreach (var mode in new[] { "off", "on", "on+verdict" })
            {
                var brainOn = mode != "off";
                var verdictAware = mode == "on+verdict";
                var label = $"{model} · {mode,-10}";
                int correct = 0, abstained = 0, wrong = 0, failed = 0;
                long totalMs = 0, promptTok = 0, evalTok = 0;
                var perQ = new List<JObject>();

                foreach (var f in facts)
                {
                    var q = f["q"]?.ToString() ?? "";
                    if (q.Length == 0) continue;

                    string prompt;
                    if (brainOn)
                    {
                        // The brain's own answer path, not a hand-tuned one: the
                        // same brain_recall a caller would run, its verdict and
                        // evidence pasted in. If recall MISSes, the model gets
                        // told that too -- hiding it would measure a retrieval
                        // system that always finds something.
                        var recall = BrainRecall(new JObject { ["query"] = q, ["limit"] = 3 }) as JObject;
                        prompt = BuildBrainPrompt(q, recall, verdictAware);
                    }
                    else
                    {
                        prompt = "Answer the question in one or two sentences. "
                               + "If you do not know, say \"I don't know\" -- do not guess.\n\n"
                               + "Question: " + q;
                    }

                    var (reply, pt, et, ms) = OllamaAsk(model, prompt);
                    totalMs += ms; promptTok += pt; evalTok += et;

                    var grade = Grade(reply, f);
                    if (grade == "correct") correct++;
                    else if (grade == "abstained") abstained++;
                    else if (grade == "failed") failed++;
                    else wrong++;

                    perQ.Add(new JObject
                    {
                        ["q"] = q,
                        ["grade"] = grade,
                        ["reply"] = Collapse(reply, 300)
                    });
                    Console.Write(grade == "correct" ? "+" : grade == "abstained" ? "."
                                : grade == "failed" ? "!" : "x");
                }

                Console.WriteLine();
                var n = facts.Count;
                Console.WriteLine($"  {label}  correct {correct,2}/{n}  abstained {abstained,2}  WRONG {wrong,2}"
                                + (failed > 0 ? $"  failed {failed,2}" : "")
                                + $"   {totalMs / (double)n / 1000:F1}s/q  {(promptTok + evalTok) / n:n0} tok/q");
                if (failed > n / 4)
                    Console.WriteLine($"  ^ {failed}/{n} calls returned nothing — treat this row as unmeasured, not as a result.");
                rows.Add(new JObject
                {
                    ["model"] = model,
                    ["brain"] = mode,
                    ["n"] = n,
                    ["correct"] = correct,
                    ["abstained"] = abstained,
                    ["wrong"] = wrong,
                    ["failed"] = failed,
                    ["msPerQuery"] = (int)(totalMs / n),
                    ["tokensPerQuery"] = (int)((promptTok + evalTok) / n),
                    ["questions"] = new JArray(perQ)
                });
            }
            Console.WriteLine();
        }

        var stamp = DateTime.UtcNow;
        var outPath = Path.Combine(_vaultPath, ".obsidianx", "eval",
            $"bench-{stamp.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}.json");
        File.WriteAllText(outPath,
            new JObject { ["ranAt"] = stamp.ToString("O"), ["serverVersion"] = ServerVersion, ["runs"] = new JArray(rows) }
                .ToString(), new UTF8Encoding(false));

        Console.WriteLine("  ── the number that matters ──");
        foreach (var model in models)
        {
            JObject? Row(string m) => rows.FirstOrDefault(r =>
                r["model"]!.ToString() == model && r["brain"]!.ToString() == m);
            var off = Row("off"); var on = Row("on"); var vd = Row("on+verdict");
            if (off == null || on == null) continue;
            Console.WriteLine($"  {model}");
            Console.WriteLine($"    correct        {off["correct"],2} → {on["correct"],2}"
                            + (vd != null ? $" → {vd["correct"],2} (+verdict)" : ""));
            Console.WriteLine($"    hallucinations {off["wrong"],2} → {on["wrong"],2}"
                            + (vd != null ? $" → {vd["wrong"],2} (+verdict)" : ""));
            Console.WriteLine($"    honest \"idk\"   {off["abstained"],2} → {on["abstained"],2}"
                            + (vd != null ? $" → {vd["abstained"],2} (+verdict)" : ""));
            Console.WriteLine($"    tokens/query   {off["tokensPerQuery"],4} → {on["tokensPerQuery"],4}"
                            + (vd != null ? $" → {vd["tokensPerQuery"],4}" : ""));
        }
        Console.WriteLine();
        Console.WriteLine($"  json: {outPath}");
        return await Task.FromResult(0);
    }

    /// <summary>
    /// The brain-on prompt. Deliberately plain: verdict, then the notes recall
    /// cited, then the same instruction the cold arm gets. Any cleverness here
    /// would be measuring the prompt rather than the brain.
    /// </summary>
    private static string BuildBrainPrompt(string q, JObject? recall, bool verdictAware = false)
    {
        var sb = new StringBuilder();
        var verdict = recall?["verdict"]?.ToString() ?? "MISS";

        sb.AppendLine("You are answering a question about a specific private codebase.");
        sb.AppendLine("Notes retrieved from that codebase's knowledge base are below.");
        sb.AppendLine("Answer ONLY from these notes. If they do not contain the answer,");
        sb.AppendLine("say \"I don't know\" -- do not guess.");
        sb.AppendLine();

        if (verdictAware)
        {
            // The verdict in words the model can act on. WEAK is the common
            // case and the dangerous one: the notes are TOPICALLY related, so
            // a model that treats "related" as "answers" produces exactly the
            // confident-wrong replies the plain arm produced.
            sb.AppendLine(verdict switch
            {
                "STRONG" => "[retrieval: STRONG — these notes were judged to directly answer this "
                          + "question (measured precision 41%, so still verify against the text).]",
                "WEAK" => "[retrieval: WEAK — these notes are on a RELATED topic but were judged "
                        + "NOT to answer the question. Expect them to be about something adjacent. "
                        + "Quote a fact only if you find it stated literally in the text below; "
                        + "otherwise say you don't know. Do not infer, combine, or approximate.]",
                _ => "[retrieval: MISS — the knowledge base holds nothing close to this question. "
                   + "The correct answer is almost certainly \"I don't know\".]"
            });
        }
        else
        {
            sb.AppendLine($"[retrieval verdict: {verdict}]");
        }
        sb.AppendLine();

        // Read the NOTE, not the snippet. The first version pasted
        // matchContext/preview and measured a brain that had already found the
        // right note: recall returned "P6 shipped" for the embedding-budget
        // question -- correct -- and the model answered "the notes do not
        // contain that information", because the snippet was the note's
        // opening paragraph and the number lives in the middle.
        //
        // That is not a benchmark artifact to paper over, it is the real
        // usage contract: brain_recall's own advice says read `answer` and
        // fetch the note when the snippet is not enough. A benchmark that
        // skips the fetch measures a caller nobody should write.
        void AddNote(JToken? n)
        {
            if (n == null) return;
            var title = n["title"]?.ToString() ?? "";
            var rel = n["path"]?.ToString() ?? "";
            var body = "";
            try
            {
                var full = Path.Combine(_vaultPath, rel);
                if (rel.Length > 0 && File.Exists(full)) body = File.ReadAllText(full);
            }
            catch { }
            if (body.Length == 0)
                body = n["matchContext"]?.ToString() ?? n["preview"]?.ToString() ?? "";
            if (title.Length == 0 && body.Length == 0) return;
            // 6,000 chars covers the great majority of this vault's notes end
            // to end; the ones past it are the same long notes section vectors
            // exist for, and truncating them here would re-create the failure
            // this comment is about.
            if (body.Length > 6000) body = body[..6000];
            sb.AppendLine($"--- {title}");
            sb.AppendLine(body);
            sb.AppendLine();
        }
        AddNote(recall?["answer"]);
        foreach (var e in recall?["evidence"] as JArray ?? new JArray()) AddNote(e);
        if (recall?["answer"] == null && (recall?["evidence"] as JArray)?.Count is null or 0)
            sb.AppendLine("(the knowledge base returned nothing)");

        sb.AppendLine("Question: " + q);
        sb.AppendLine("Answer in one or two sentences.");
        return sb.ToString();
    }

    /// <summary>
    /// correct beats abstained beats wrong, and `never` overrides everything —
    /// a reply that contains both the right answer and a forbidden one (the
    /// classic "2025.1.0, or possibly 2026.0.0") is not a win.
    /// </summary>
    private static string Grade(string reply, JObject fact)
    {
        // An empty reply is a FAILED CALL, not a hallucination. gemma3:27b on
        // this box returned nothing on every question at exactly the 180 s
        // timeout, and the first version of this function scored that as
        // "wrong 24/24" -- a chart claiming the biggest model invents an
        // answer every time, when it had not answered at all. Any benchmark
        // that cannot tell silence from error will eventually publish that.
        if (string.IsNullOrWhiteSpace(reply)) return "failed";

        var r = reply.ToLowerInvariant();
        foreach (var bad in fact["never"] as JArray ?? new JArray())
            if (r.Contains(bad.ToString().ToLowerInvariant(), StringComparison.Ordinal)) return "wrong";
        foreach (var good in fact["any"] as JArray ?? new JArray())
            if (r.Contains(good.ToString().ToLowerInvariant(), StringComparison.Ordinal)) return "correct";
        foreach (var idk in IdkMarkers)
            if (r.Contains(idk, StringComparison.OrdinalIgnoreCase)) return "abstained";
        return "wrong";
    }

    /// <summary>
    /// Plain-text Ollama chat that also returns the token counts the daemon
    /// reports. Separate from OllamaJsonChat because forcing format=json on a
    /// prose answer changes what the model says, and this benchmark is about
    /// what it says.
    /// </summary>
    private static (string Reply, long PromptTokens, long EvalTokens, long Ms) OllamaAsk(string model, string prompt)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(180) };
            var body = new JObject
            {
                ["model"] = model,
                ["stream"] = false,
                ["messages"] = new JArray { new JObject { ["role"] = "user", ["content"] = prompt } },
                ["options"] = new JObject { ["temperature"] = 0.1, ["num_predict"] = 300 }
            }.ToString();
            var resp = http.PostAsync("http://localhost:11434/api/chat",
                new System.Net.Http.StringContent(body, Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();
            sw.Stop();
            if (!resp.IsSuccessStatusCode) return ("", 0, 0, sw.ElapsedMilliseconds);
            var raw = JObject.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return (raw["message"]?["content"]?.ToString() ?? "",
                    raw["prompt_eval_count"]?.Value<long>() ?? 0,
                    raw["eval_count"]?.Value<long>() ?? 0,
                    sw.ElapsedMilliseconds);
        }
        catch { sw.Stop(); return ("", 0, 0, sw.ElapsedMilliseconds); }
    }
}
