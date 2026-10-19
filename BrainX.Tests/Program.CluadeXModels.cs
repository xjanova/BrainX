using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// CluadeX in the cowork room's model panel (CluadeXModels + CluadeXClient's parses). Pure and offline: the replies are
/// the JSON CluadeX's pipe sends (cluadex.get_active_model / cluadex.choose_model), the row is what office.js draws.
/// </summary>
internal static partial class Program
{
    private static void RegisterCluadeXModelChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("cluadex models: the report carries the model box, the effort and its levels; an old one still parses", CluadeXReportParse));
        checks.Add(("cluadex models: the panel row shows what CluadeX answers with now, never offers a model on that PC", CluadeXRow));
        checks.Add(("cluadex models: a pick is only asked of CluadeX when it offered it; its verdict or refusal comes back", CluadeXPick));
    }

    /// <summary>What CluadeX 3.0.74+ answers: the chat on Claude Sonnet, two API models to pick, effort medium.</summary>
    private const string CluadeXReport = """
        {"provider":"Anthropic","model":"claude-sonnet-5-5","path":"","ready":true,"vramUsedMB":3200,"vramTotalMB":24576,
         "effort":"medium",
         "options":[{"provider":"Anthropic","model":"claude-sonnet-5-5","label":"Anthropic Claude","price":"$2 / $10"},
                    {"provider":"Anthropic","model":"claude-haiku-5-5","label":"Anthropic Claude","price":"$0.8 / $4"}],
         "efforts":["auto","low","medium","high","xhigh","max"],"note":"OK"}
        """;

    private static Task CluadeXReportParse()
    {
        var info = CluadeXClient.ParseActiveModel(CluadeXReport);
        Check("model and provider", info.Provider == "Anthropic" && info.Model == "claude-sonnet-5-5");
        Check("the model box comes across", info.Options.Count == 2 && info.Options[1].Model == "claude-haiku-5-5"
              && info.Options[1].Price == "$0.8 / $4" && info.Options[0].Label == "Anthropic Claude");
        Check("effort and its levels", info.Effort == "medium" && info.Efforts.Count == 6);

        var old = CluadeXClient.ParseActiveModel("""{"provider":"Local","model":"qwen2.5-coder-7b","ready":true,"note":"OK"}""");
        Check("a CluadeX from before: no options, no effort, no throw", old.Model == "qwen2.5-coder-7b" && old.Options.Count == 0 && old.Efforts.Count == 0 && old.Effort == "");
        Check("garbage: a note, not an exception", CluadeXClient.ParseActiveModel("not json").Note.StartsWith("unparseable"));
        return Task.CompletedTask;
    }

    private static Task CluadeXRow()
    {
        var row = CluadeXModels.Row(CluadeXClient.ParseActiveModel(CluadeXReport));
        Check("an app row under CluadeX's bus name", (string?)row["agent"] == "cluadex" && (string?)row["app"] == "CluadeX");
        Check("chosen = what it answers with now", (string?)row["chosen"] == "claude-sonnet-5-5" && (string?)row["current"] == "claude-sonnet-5-5");
        Check("the two API models are offered, labelled and priced", row["options"] is JArray o && o.Count == 2
              && (string?)o[0]!["label"] == "Anthropic Claude · claude-sonnet-5-5" && ((string?)o[1]!["note"] ?? "").Contains("$0.8 / $4"));
        Check("its auto level is the default, not a pick", (string?)row["effortChosen"] == "medium"
              && row["effortOptions"] is JArray e && e.Count == 5 && !e.Any(x => (string?)x["id"] == "auto"));
        Check("nothing a runner has: no runners.json default, no broker run", (string?)row["configured"] == "" && (string?)row["running"] == ""
              && row["onCall"]!.ToObject<bool>() == false && row["canChoose"]!.ToObject<bool>());

        var local = CluadeXModels.Row(CluadeXClient.ParseActiveModel(CluadeXReport
            .Replace("\"provider\":\"Anthropic\",\"model\":\"claude-sonnet-5-5\",\"path\"", "\"provider\":\"LlamaServer\",\"model\":\"qwen2.5-coder-7b-q4_k_m\",\"path\"")
            .Replace("\"effort\":\"medium\"", "\"effort\":\"auto\"")));
        Check("a model on that PC answers: shown as current, not chosen — the picks stay API models",
              (string?)local["current"] == "qwen2.5-coder-7b-q4_k_m" && (string?)local["chosen"] == "" && (string?)local["currentProvider"] == "LlamaServer"
              && local["options"] is JArray lo && lo.All(x => (string?)x["id"] != "qwen2.5-coder-7b-q4_k_m"));
        Check("auto effort reads as no pick", (string?)local["effortChosen"] == "");

        var bare = CluadeXModels.Row(CluadeXClient.ParseActiveModel("""{"provider":"Local","model":"x"}"""), "เปลี่ยนไม่ได้");
        Check("no API key in CluadeX: nothing to choose, the refusal note rides along",
              bare["canChoose"]!.ToObject<bool>() == false && bare["effortCanChoose"]!.ToObject<bool>() == false && (string?)bare["note"] == "เปลี่ยนไม่ได้");
        return Task.CompletedTask;
    }

    private static Task CluadeXPick()
    {
        var info = CluadeXClient.ParseActiveModel(CluadeXReport);
        Check("an offered model / effort is asked", CluadeXModels.IsOffered(info, "claude-haiku-5-5", null) && CluadeXModels.IsOffered(info, null, "high")
              && CluadeXModels.IsOffered(info, null, CluadeXModels.AutoEffort));
        Check("anything else never reaches the pipe", !CluadeXModels.IsOffered(info, "qwen2.5-coder-7b-q4_k_m", null)
              && !CluadeXModels.IsOffered(info, "--dangerously-skip", null) && !CluadeXModels.IsOffered(info, null, "turbo"));

        var ok = CluadeXClient.ParseChoose("""{"ok":true,"model":"claude-haiku-5-5","effort":"high","reason":""}""");
        Check("the verdict", ok.Ok && ok.Model == "claude-haiku-5-5" && ok.Effort == "high");
        var no = CluadeXClient.ParseChoose("""{"ok":false,"model":"claude-sonnet-5-5","effort":"medium","reason":"มีคำตอบกำลังรันอยู่"}""");
        Check("a refusal keeps CluadeX's reason", !no.Ok && no.Reason == "มีคำตอบกำลังรันอยู่");
        var unknown = CluadeXClient.ParseChoose("Unknown tool 'cluadex.choose_model'");
        Check("a CluadeX without the tool: its error text is the reason", !unknown.Ok && unknown.Reason.Contains("Unknown tool"));
        return Task.CompletedTask;
    }
}
