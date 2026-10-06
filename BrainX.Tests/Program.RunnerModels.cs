using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// The model a broker-started agent runs on (RunnerModels). Pure and offline:
/// the arg template the broker builds, and the models.json the room writes.
/// </summary>
internal static partial class Program
{
    private static void RegisterRunnerModelChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("runner models: the model goes in front of the first option, never behind a list option", RunnerModelArgs));
        checks.Add(("runner models: a pick round-trips and a flag dressed as a model never does", RunnerModelChoices));
        checks.Add(("runner effort: each CLI is told its level its own way, and only a plain level ever goes", RunnerEffortArgs));
        checks.Add(("bus seal: a sealed line is only the owner's under the name it was written as", SealWrittenAsChecks));
    }

    private static Task SealWrittenAsChecks()
    {
        var now = DateTime.UtcNow;
        var line = new JObject { ["id"] = $"c-{now.Ticks}-abcdef", ["from"] = "owner", ["body"] = "x" };
        Check("written a moment before its file: genuine",
            BusSeal.WrittenAs(line, $"{now.AddMilliseconds(40).Ticks:D19}-owner-ab12.json"));
        Check("an hour-old line under a fresh name: a copy",
            !BusSeal.WrittenAs(line, $"{now.AddHours(1).Ticks:D19}-owner-ab12.json"));
        Check("no tick in the id: not provable",
            !BusSeal.WrittenAs(new JObject { ["id"] = "c-x-abcdef" }, $"{now.Ticks:D19}-owner-ab12.json"));
        Check("no tick in the name: not provable", !BusSeal.WrittenAs(line, "owner.json"));
        return Task.CompletedTask;
    }

    private static string ArgLine(IEnumerable<string> args) => string.Join(" ", args);

    private static Task RunnerModelArgs()
    {
        var claude = new[] { "-p", "{prompt}", "--allowedTools", "mcp__brainx-brain" };
        var codex = new[] { "exec", "--skip-git-repo-check", "--approve-for-me", "-C", "{cwd}", "{prompt}" };

        var line = ArgLine(RunnerModels.ApplyToArgs(claude, "claude-sonnet-5-5", RunnerModels.DefaultFlag));
        Check("claude: --model goes before -p, so --allowedTools cannot swallow it",
            line == "--model claude-sonnet-5-5 -p {prompt} --allowedTools mcp__brainx-brain", line);

        line = ArgLine(RunnerModels.ApplyToArgs(codex, "gpt-6.1-sol", RunnerModels.DefaultFlag));
        Check("codex: --model goes after the exec subcommand",
            line == "exec --model gpt-6.1-sol --skip-git-repo-check --approve-for-me -C {cwd} {prompt}", line);

        Check("no model: the template is untouched",
            ArgLine(RunnerModels.ApplyToArgs(claude, null, RunnerModels.DefaultFlag)) == ArgLine(claude));
        Check("an empty flag: nothing is inserted",
            ArgLine(RunnerModels.ApplyToArgs(claude, "claude-opus-5-5", "")) == ArgLine(claude));

        line = ArgLine(RunnerModels.ApplyToArgs(claude, "--dangerously-skip-permissions", RunnerModels.DefaultFlag));
        Check("a flag passed as a model is dropped, not put on the command line", line == ArgLine(claude), line);
        Check("a model with a space or a placeholder is not a model",
            !RunnerModels.IsValidId("gpt 6") && !RunnerModels.IsValidId("{prompt}") && !RunnerModels.IsValidId(""));

        var placed = new[] { "run", "-m", "{model}", "{prompt}" };
        line = ArgLine(RunnerModels.ApplyToArgs(placed, "x-1", RunnerModels.DefaultFlag));
        Check("{model} in the template: substituted where it stands", line == "run -m x-1 {prompt}", line);
        line = ArgLine(RunnerModels.ApplyToArgs(placed, null, RunnerModels.DefaultFlag));
        Check("{model} with no model: the flag in front of it goes too", line == "run {prompt}", line);
        line = ArgLine(RunnerModels.ApplyToArgs(new[] { "run", "--model={model}", "{prompt}" }, null, RunnerModels.DefaultFlag));
        Check("--model={model} with no model: dropped whole", line == "run {prompt}", line);

        line = ArgLine(RunnerModels.ApplyToArgs(new[] { "exec", "{prompt}" }, "m1", RunnerModels.DefaultFlag));
        Check("no options at all: the model goes in front of the prompt", line == "exec --model m1 {prompt}", line);

        Check("a runner with an empty flag and no placeholder cannot be told a model",
            !RunnerModels.CanChoose(claude, "") && RunnerModels.CanChoose(claude, "--model") && RunnerModels.CanChoose(placed, ""));
        return Task.CompletedTask;
    }

    private static Task RunnerEffortArgs()
    {
        var claude = new[] { "-p", "{prompt}", "--allowedTools", "mcp__brainx-brain" };
        var codex = new[] { "exec", "--skip-git-repo-check", "-C", "{cwd}", "{prompt}" };
        var claudeEffort = RunnerModels.DefaultEffortArgs("claude", @"C:\x\claude.exe");
        var codexEffort = RunnerModels.DefaultEffortArgs("codex", "codex");

        var line = ArgLine(RunnerModels.ApplyEffortToArgs(
            RunnerModels.ApplyToArgs(claude, "claude-opus-5-5", RunnerModels.DefaultFlag), "high", claudeEffort));
        Check("claude: --effort and --model both go before -p",
            line == "--effort high --model claude-opus-5-5 -p {prompt} --allowedTools mcp__brainx-brain", line);

        line = ArgLine(RunnerModels.ApplyEffortToArgs(codex, "xhigh", codexEffort));
        Check("codex: a config override after the exec subcommand",
            line == "exec -c model_reasoning_effort=xhigh --skip-git-repo-check -C {cwd} {prompt}", line);

        Check("no effort: the template is untouched",
            ArgLine(RunnerModels.ApplyEffortToArgs(codex, null, codexEffort)) == ArgLine(codex));
        line = ArgLine(RunnerModels.ApplyEffortToArgs(codex, "high,sandbox_mode=danger-full-access", codexEffort));
        Check("a level carrying more config is not a level", line == ArgLine(codex), line);
        Check("levels are plain lower-case words",
            RunnerModels.IsValidEffort("xhigh") && !RunnerModels.IsValidEffort("-x") && !RunnerModels.IsValidEffort("\"high\"")
            && !RunnerModels.IsValidEffort("High") && !RunnerModels.IsValidEffort(""));

        var placed = new[] { "exec", "-c", "model_reasoning_effort={effort}", "{prompt}" };
        line = ArgLine(RunnerModels.ApplyEffortToArgs(placed, null, codexEffort));
        Check("{effort} as an option's value with no level: the option goes too", line == "exec {prompt}", line);
        line = ArgLine(RunnerModels.ApplyEffortToArgs(placed, "low", Array.Empty<string>()));
        Check("{effort} in the template: substituted where it stands", line == "exec -c model_reasoning_effort=low {prompt}", line);

        Check("a CLI it does not know takes no effort until runners.json says how",
            RunnerModels.DefaultEffortArgs("gemini", "gemini").Count == 0
            && !RunnerModels.CanChooseEffort(claude, Array.Empty<string>()) && RunnerModels.CanChooseEffort(claude, claudeEffort));

        var opus = RunnerModels.SupportedEfforts("claude", "claude", "claude-opus-5-5", null, out var opusDefault);
        Check("claude Opus 5.5: five levels", ArgLine(opus.Select(o => o.Id)) == "low medium high xhigh max", ArgLine(opus.Select(o => o.Id)));
        var haiku = RunnerModels.SupportedEfforts("claude", "claude", "claude-haiku-4-5", null, out var haikuDefault);
        Check("claude Haiku 4.5: no effort at all", haiku.Count == 0 && haikuDefault == null);
        var mine = RunnerModels.SupportedEfforts("gemini", "gemini", null, JArray.Parse("[\"low\",\"-x\",\"high\"]"), out _);
        Check("runners.json's own levels, bad ones skipped", ArgLine(mine.Select(o => o.Id)) == "low high", ArgLine(mine.Select(o => o.Id)));
        return Task.CompletedTask;
    }

    private static Task RunnerModelChoices()
    {
        var bus = Path.Combine(Path.GetTempPath(), "brainx-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            Check("no file: no pick", RunnerModels.ReadChoice(bus, "claude") == null);

            RunnerModels.WriteChoice(bus, "claude", "claude-opus-5-5");
            RunnerModels.WriteChoice(bus, "codex", "gpt-6.1-sol");
            Check("a pick reads back", RunnerModels.ReadChoice(bus, "claude") == "claude-opus-5-5");
            Check("the agent name is case-insensitive", RunnerModels.ReadChoice(bus, "Claude") == "claude-opus-5-5");

            RunnerModels.WriteChoice(bus, "CLAUDE", "claude-haiku-4-5");
            var o = JObject.Parse(File.ReadAllText(RunnerModels.ChoicePath(bus)));
            Check("one key per agent whatever the case", o.Properties().Count() == 2, o.ToString());
            Check("the newer pick wins", RunnerModels.ReadChoice(bus, "claude") == "claude-haiku-4-5");

            RunnerModels.WriteChoice(bus, "claude", null);
            Check("clearing one pick leaves the other",
                RunnerModels.ReadChoice(bus, "claude") == null && RunnerModels.ReadChoice(bus, "codex") == "gpt-6.1-sol");

            var threw = false;
            try { RunnerModels.WriteChoice(bus, "claude", "--yolo"); } catch (ArgumentException) { threw = true; }
            Check("writing a flag as a model is refused", threw);
            Check("no temp file is left behind",
                Directory.GetFiles(Path.GetDirectoryName(RunnerModels.ChoicePath(bus))!, "*.tmp").Length == 0);

            File.WriteAllText(RunnerModels.ChoicePath(bus), "{\"claude\":\"-x\",\"codex\":\"gpt 6\"}");
            Check("a hand-edited bad value reads as no pick",
                RunnerModels.ReadChoice(bus, "claude") == null && RunnerModels.ReadChoice(bus, "codex") == null);
            File.WriteAllText(RunnerModels.ChoicePath(bus), "{not json");
            Check("a broken file reads as no pick, not an exception", RunnerModels.ReadChoice(bus, "codex") == null);

            var claude = RunnerModels.Supported("claude", "claude", null, out _);
            Check("claude offers the current generation", claude.Any(m => m.Id == "claude-opus-5-5") && claude.All(m => RunnerModels.IsValidId(m.Id)));

            var mine = RunnerModels.Supported("gemini", "gemini",
                JArray.Parse("[\"gemini-pro\", {\"id\":\"gemini-flash\",\"label\":\"Flash\"}, \"--bad\", \"gemini-pro\"]"), out _);
            Check("runners.json's own list wins, bad and duplicate ids skipped",
                ArgLine(mine.Select(m => m.Id)) == "gemini-pro gemini-flash" && mine[1].Label == "Flash",
                ArgLine(mine.Select(m => m.Id)));
            Check("an unknown CLI with no list offers nothing",
                RunnerModels.Supported("gemini", "gemini", null, out _).Count == 0);

            RunnerModels.WriteEffortChoice(bus, "claude", "high");
            Check("an effort pick reads back, beside the model pick",
                RunnerModels.ReadEffortChoice(bus, "CLAUDE") == "high" && File.Exists(RunnerModels.EffortPath(bus)));
            threw = false;
            try { RunnerModels.WriteEffortChoice(bus, "claude", "high --yolo"); } catch (ArgumentException) { threw = true; }
            Check("writing more than a level is refused", threw && RunnerModels.ReadEffortChoice(bus, "claude") == "high");
            RunnerModels.WriteEffortChoice(bus, "claude", "");
            Check("clearing the effort pick", RunnerModels.ReadEffortChoice(bus, "claude") == null);
        }
        finally { try { Directory.Delete(bus, true); } catch { } }
        return Task.CompletedTask;
    }
}
