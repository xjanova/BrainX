using BrainX.Core.Services;

namespace BrainX.Tests;

/// <summary>
/// Mind's settings file and what she reads aloud (AssistantService). Offline:
/// a scratch vault, no Ollama, no TTS.
/// </summary>
internal static partial class Program
{
    private static void RegisterAssistantChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("mind: a broken assistant.json is kept aside, never saved over", AssistantBrokenConfigKept));
        checks.Add(("mind: a long Thai answer is cut at a space, never inside a word", AssistantSpeakableThaiCut));
    }

    private static Task AssistantBrokenConfigKept()
    {
        var vault = Path.Combine(Path.GetTempPath(), "brainx-mind-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
        try
        {
            var svc = new AssistantService(vault, "");
            var broken = "{ \"Voice\": \"en-US-AriaNeural\", \"AutoStart\": true,, }";
            File.WriteAllText(svc.ConfigPath, broken);

            var cfg = svc.LoadConfig();
            Check("broken file reads as defaults", cfg.Voice == new AssistantConfig().Voice);
            var kept = Directory.GetFiles(Path.GetDirectoryName(svc.ConfigPath)!, "assistant.json.bad-*");
            Check("the owner's text is kept beside it", kept.Length == 1 && File.ReadAllText(kept[0]) == broken);

            cfg.AutoStart = true;
            svc.SaveConfig(cfg);
            Check("a save round-trips", svc.LoadConfig().AutoStart);
            Check("no temp file left behind",
                Directory.GetFiles(Path.GetDirectoryName(svc.ConfigPath)!, "assistant.json.tmp-*").Length == 0);
        }
        finally { try { Directory.Delete(vault, true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task AssistantSpeakableThaiCut()
    {
        // 60 Thai words of 12 letters, spaces between, no full stop anywhere.
        var word = "สวัสดีครับผม";
        var text = string.Join(" ", Enumerable.Repeat(word, 60));
        var spoken = AssistantService.Speakable(text);
        Check("capped", spoken.Length < 640, spoken.Length.ToString());
        var body = spoken.TrimEnd('…', ' ');
        Check("ends on a whole word", body.EndsWith(word, StringComparison.Ordinal), body[^20..]);

        var en = "One sentence here. " + new string('x', 300) + ". Another one that runs long " + new string('y', 400);
        Check("an English full stop still wins", AssistantService.Speakable(en).TrimEnd('…', ' ').EndsWith(".", StringComparison.Ordinal));
        return Task.CompletedTask;
    }
}
