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
        }
        finally { try { Directory.Delete(bus, true); } catch { } }
        return Task.CompletedTask;
    }
}
