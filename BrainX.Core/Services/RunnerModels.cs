using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Which model an agent the broker starts runs on.
///
/// Owner (2026-10-04): "ให้เลือกโมเดลที่ซัพพอตเพื่อเข้าทำงานได้ มีตัวเลือก".
///
/// A session the owner opened runs on whatever its own app was set to; the
/// room cannot reach into it. What the room CAN decide is the model of a run
/// the broker starts — `claude -p` / `codex exec` when an order lands in a room
/// nobody is sitting in, or an idle study. Until now that was whatever each
/// CLI defaulted to, and changing it meant hand-editing runners.json and
/// restarting the broker.
///
///   agent-bus/cowork/models.json   { "claude": "claude-sonnet-5-5", ... }
///
/// Written by the client when the owner picks from the room, read by the
/// broker at every spawn — so a pick applies to the next run without a
/// restart. Shared here so both sides agree on the file, on what a model id
/// may look like, and on where the flag goes on the command line.
/// </summary>
public static class RunnerModels
{
    public sealed record Option(string Id, string Label, string Note);

    /// <summary>Both CLIs the broker ships with take <c>--model</c>
    /// (<c>codex exec -m/--model</c>, <c>claude --model</c>).</summary>
    public const string DefaultFlag = "--model";

    /// <summary>The placeholder a runner's args can carry to say exactly where
    /// the model goes. An arg holding it is dropped when no model is chosen.</summary>
    public const string Placeholder = "{model}";

    public static string ChoicePath(string busRoot) => Path.Combine(busRoot, "cowork", "models.json");

    /// <summary>
    /// A model id is the VALUE of one option and must never be able to become
    /// an option itself. It reaches the command line of a process that runs
    /// with write access to the owner's repos: "--dangerously-skip-permissions"
    /// in models.json would otherwise be a flag, not a model. No leading dash,
    /// no spaces, no braces (so it cannot smuggle a {prompt} placeholder).
    /// </summary>
    private static readonly Regex IdPattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._:/@\[\]-]{0,99}$", RegexOptions.CultureInvariant);

    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);

    /// <summary>
    /// Claude has no local list to read, so the current generation is named
    /// here (IDs as the Claude API publishes them). runners.json can replace it
    /// per runner with a <c>models</c> array without a rebuild.
    /// </summary>
    public static readonly IReadOnlyList<Option> ClaudeModels = new[]
    {
        new Option("claude-fable-5-1", "Fable 5.1", "เก่งที่สุด — งานยาก งานยาว ราคาสูงสุด"),
        new Option("claude-opus-5-5", "Opus 5.5", "เก่งรอบด้าน สมดุลระหว่างฝีมือกับราคา"),
        new Option("claude-sonnet-5-5", "Sonnet 5.5", "เร็วและคุ้ม — งานโค้ดประจำวัน"),
        new Option("claude-haiku-4-5", "Haiku 4.5", "เร็วสุด ถูกสุด — งานง่าย ๆ"),
    };

    // ───────────── the owner's picks ─────────────

    /// <summary>The model the owner picked for this agent, or null for "the
    /// CLI's own default". An invalid value on disk reads as no pick.</summary>
    public static string? ReadChoice(string busRoot, string agent)
    {
        var v = ReadChoices(busRoot).TryGetValue(agent, out var m) ? m : null;
        return IsValidId(v) ? v : null;
    }

    public static Dictionary<string, string> ReadChoices(string busRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = ChoicePath(busRoot);
            if (!File.Exists(path)) return map;
            if (JToken.Parse(File.ReadAllText(path)) is not JObject o) return map;
            foreach (var (k, v) in o)
                if (v?.Type == JTokenType.String && IsValidId(v.ToString())) map[k] = v.ToString();
        }
        catch { /* a broken file is "no picks", never a crash in the broker */ }
        return map;
    }

    /// <summary>Record a pick; null or empty clears it back to the CLI default.
    /// temp + move, like every other file on the bus: the broker reads this at
    /// spawn time and must never see half a document.</summary>
    public static void WriteChoice(string busRoot, string agent, string? model)
    {
        if (string.IsNullOrWhiteSpace(agent)) throw new ArgumentException("agent is required", nameof(agent));
        if (!string.IsNullOrEmpty(model) && !IsValidId(model))
            throw new ArgumentException($"'{model}' is not a model id", nameof(model));

        var path = ChoicePath(busRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var o = new JObject();
        foreach (var (k, v) in ReadChoices(busRoot)) o[k] = v;
        // One key per agent whatever case it was written in.
        foreach (var k in o.Properties().Select(p => p.Name)
                     .Where(n => n.Equals(agent, StringComparison.OrdinalIgnoreCase)).ToList())
            o.Remove(k);
        if (!string.IsNullOrEmpty(model)) o[agent] = model;

        var tmp = path + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
        File.WriteAllText(tmp, o.ToString(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    // ───────────── the command line ─────────────

    /// <summary>
    /// A runner's arg template with the model put in.
    ///
    /// When the template says where (an arg containing {model}), it goes
    /// there; with no model that arg is dropped, and a bare "{model}" takes
    /// the flag in front of it along. Otherwise <paramref name="flag"/> and the
    /// model go in front of the first option or the prompt — after a leading
    /// subcommand (`codex exec --model x …`) and before an option that takes a
    /// list (`claude --allowedTools a b` would swallow anything after it).
    /// </summary>
    public static List<string> ApplyToArgs(IReadOnlyList<string> args, string? model, string? flag)
    {
        var chosen = IsValidId(model) ? model : null;
        var result = new List<string>(args.Count + 2);

        if (args.Any(a => a.Contains(Placeholder, StringComparison.Ordinal)))
        {
            foreach (var a in args)
            {
                if (!a.Contains(Placeholder, StringComparison.Ordinal)) { result.Add(a); continue; }
                if (chosen != null) { result.Add(a.Replace(Placeholder, chosen, StringComparison.Ordinal)); continue; }
                if (a == Placeholder && result.Count > 0 && result[^1].StartsWith('-')) result.RemoveAt(result.Count - 1);
            }
            return result;
        }

        result.AddRange(args);
        if (chosen == null || string.IsNullOrWhiteSpace(flag)) return result;

        var at = result.FindIndex(a => a.StartsWith('-') || a.Contains("{prompt}", StringComparison.Ordinal));
        if (at < 0) at = result.Count;
        result.InsertRange(at, new[] { flag.Trim(), chosen });
        return result;
    }

    /// <summary>Can this runner be told a model at all?</summary>
    public static bool CanChoose(IReadOnlyList<string> args, string? flag) =>
        args.Any(a => a.Contains(Placeholder, StringComparison.Ordinal)) || !string.IsNullOrWhiteSpace(flag);

    // ───────────── what each runner supports ─────────────

    /// <summary>
    /// The models a runner can be started on, most capable first, and the one
    /// its CLI uses when nothing is passed (null when that cannot be read).
    ///
    /// runners.json's <c>models</c> wins — the owner knows their line-up. Then
    /// codex's own catalogue (the cache the CLI keeps of what this account can
    /// use), then the Claude list above. Anything else: nothing to offer.
    /// </summary>
    public static IReadOnlyList<Option> Supported(string agent, string exe, JToken? declared, out string? cliDefault)
    {
        var vendor = Vendor(agent, exe);
        cliDefault = vendor switch
        {
            "codex" => CodexDefault(),
            "claude" => ClaudeDefault(),
            _ => null,
        };

        var mine = ParseDeclared(declared);
        if (mine.Count > 0) return mine;

        return vendor switch
        {
            "codex" => CodexCatalogue(),
            "claude" => ClaudeModels,
            _ => Array.Empty<Option>(),
        };
    }

    private static string Vendor(string agent, string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant();
        if (name is "codex" or "claude") return name;
        var a = (agent ?? "").ToLowerInvariant();
        return a.StartsWith("codex", StringComparison.Ordinal) ? "codex"
             : a.StartsWith("claude", StringComparison.Ordinal) ? "claude"
             : "";
    }

    private static List<Option> ParseDeclared(JToken? declared)
    {
        var list = new List<Option>();
        if (declared is not JArray arr) return list;
        foreach (var t in arr)
        {
            string? id, label = null, note = null;
            if (t.Type == JTokenType.String) id = t.ToString();
            else if (t is JObject o)
            {
                id = o["id"]?.ToString();
                label = o["label"]?.ToString();
                note = o["note"]?.ToString();
            }
            else continue;
            if (!IsValidId(id) || list.Any(x => x.Id == id)) continue;
            list.Add(new Option(id!, string.IsNullOrWhiteSpace(label) ? id! : label!, note ?? ""));
        }
        return list;
    }

    private static string CodexHome() =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private static string ClaudeHome() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>
    /// codex keeps the catalogue it was served in models_cache.json — the
    /// models THIS account can use, with the ones Codex itself hides marked
    /// `visibility: hide`. 400 KB, so read once per write of the file.
    /// </summary>
    private static IReadOnlyList<Option> CodexCatalogue()
    {
        var path = Path.Combine(CodexHome(), "models_cache.json");
        return Cached<IReadOnlyList<Option>>(path, () =>
        {
            var list = new List<(int Priority, Option O)>();
            if (JToken.Parse(File.ReadAllText(path))["models"] is not JArray models) return Array.Empty<Option>();
            foreach (var m in models.OfType<JObject>())
            {
                var id = m["slug"]?.ToString();
                if (!IsValidId(id)) continue;
                if (string.Equals(m["visibility"]?.ToString(), "hide", StringComparison.OrdinalIgnoreCase)) continue;
                var label = m["display_name"]?.ToString() is { Length: > 0 } d ? d : id!;
                list.Add((m["priority"]?.ToObject<int?>() ?? int.MaxValue,
                          new Option(id!, label, m["description"]?.ToString() ?? "")));
            }
            return list.OrderBy(x => x.Priority).Select(x => x.O).ToList();
        }) ?? Array.Empty<Option>();
    }

    /// <summary>The top-level <c>model = "…"</c> of codex's config.toml —
    /// before the first [table], because a profile's model is not the default.</summary>
    private static string? CodexDefault()
    {
        var path = Path.Combine(CodexHome(), "config.toml");
        return Cached<string>(path, () =>
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) break;
                var m = Regex.Match(line, @"^model\s*=\s*[""']([^""']+)[""']", RegexOptions.CultureInvariant);
                if (m.Success && IsValidId(m.Groups[1].Value)) return m.Groups[1].Value;
            }
            return null;
        });
    }

    /// <summary>Claude Code's <c>model</c> setting, when the owner set one.</summary>
    private static string? ClaudeDefault()
    {
        var path = Path.Combine(ClaudeHome(), "settings.json");
        return Cached<string>(path, () =>
            JToken.Parse(File.ReadAllText(path))["model"]?.ToString() is { } m && IsValidId(m) ? m : null);
    }

    private static readonly Dictionary<string, (DateTime Stamp, object? Value)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A value derived from one file, recomputed only when the file
    /// changes. Null when the file is missing or unreadable.</summary>
    private static T? Cached<T>(string path, Func<T?> read) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (_cache)
                if (_cache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Value as T;
            var value = read();
            lock (_cache) _cache[path] = (stamp, value);
            return value;
        }
        catch { return null; }
    }
}
