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

    public static Dictionary<string, string> ReadChoices(string busRoot) => ReadMap(ChoicePath(busRoot), IsValidId);

    /// <summary>Record a pick; null or empty clears it back to the CLI default.</summary>
    public static void WriteChoice(string busRoot, string agent, string? model)
    {
        if (!string.IsNullOrEmpty(model) && !IsValidId(model))
            throw new ArgumentException($"'{model}' is not a model id", nameof(model));
        WriteMap(ChoicePath(busRoot), agent, model, IsValidId);
    }

    private static Dictionary<string, string> ReadMap(string path, Func<string, bool> valid)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return map;
            if (JToken.Parse(File.ReadAllText(path)) is not JObject o) return map;
            foreach (var (k, v) in o)
                if (v?.Type == JTokenType.String && valid(v.ToString())) map[k] = v.ToString();
        }
        catch { /* a broken file is "no picks", never a crash in the broker */ }
        return map;
    }

    /// <summary>temp + move, like every other file on the bus: the broker reads
    /// these at spawn time and must never see half a document.</summary>
    private static void WriteMap(string path, string agent, string? value, Func<string, bool> valid)
    {
        if (string.IsNullOrWhiteSpace(agent)) throw new ArgumentException("agent is required", nameof(agent));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var o = new JObject();
        foreach (var (k, v) in ReadMap(path, valid)) o[k] = v;
        // One key per agent whatever case it was written in.
        foreach (var k in o.Properties().Select(p => p.Name)
                     .Where(n => n.Equals(agent, StringComparison.OrdinalIgnoreCase)).ToList())
            o.Remove(k);
        if (!string.IsNullOrEmpty(value)) o[agent] = value;

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
    public static List<string> ApplyToArgs(IReadOnlyList<string> args, string? model, string? flag) =>
        Apply(args, Placeholder, IsValidId(model) ? model : null,
              string.IsNullOrWhiteSpace(flag) ? Array.Empty<string>() : new[] { flag.Trim(), Placeholder });

    /// <summary>Can this runner be told a model at all?</summary>
    public static bool CanChoose(IReadOnlyList<string> args, string? flag) =>
        args.Any(a => a.Contains(Placeholder, StringComparison.Ordinal)) || !string.IsNullOrWhiteSpace(flag);

    /// <summary>
    /// <paramref name="value"/> put into <paramref name="args"/>: where the
    /// template has <paramref name="placeholder"/>, else <paramref name="insert"/>
    /// (with the placeholder filled) in front of the first option or the prompt.
    /// With no value an arg holding the placeholder is dropped, and when that
    /// arg was the VALUE of an option (`-m {model}`, `-c key={effort}`) the
    /// option goes with it rather than being left to swallow the next arg.
    /// </summary>
    private static List<string> Apply(IReadOnlyList<string> args, string placeholder, string? value,
                                      IReadOnlyList<string> insert)
    {
        var result = new List<string>(args.Count + insert.Count);

        if (args.Any(a => a.Contains(placeholder, StringComparison.Ordinal)))
        {
            foreach (var a in args)
            {
                if (!a.Contains(placeholder, StringComparison.Ordinal)) { result.Add(a); continue; }
                if (value != null) { result.Add(a.Replace(placeholder, value, StringComparison.Ordinal)); continue; }
                if (!a.StartsWith('-') && result.Count > 0 && result[^1].StartsWith('-')) result.RemoveAt(result.Count - 1);
            }
            return result;
        }

        result.AddRange(args);
        if (value == null || !insert.Any(a => a.Contains(placeholder, StringComparison.Ordinal))) return result;

        var at = result.FindIndex(a => a.StartsWith('-') || a.Contains("{prompt}", StringComparison.Ordinal));
        if (at < 0) at = result.Count;
        result.InsertRange(at, insert.Select(a => a.Replace(placeholder, value, StringComparison.Ordinal)));
        return result;
    }

    // ───────────── effort ─────────────
    //
    // Owner (2026-10-06): "ในหมวด cowork room model มันถูกกำหนด effort ไว้เท่าไหร่
    // เราทำให้ตั้งได้ด้วย ตรง model". Until then no run the broker started was
    // told an effort at all: claude ran at its model's own default (medium on
    // Opus 5.5) and codex at config.toml's model_reasoning_effort.
    //
    //   agent-bus/cowork/efforts.json   { "claude": "high", "codex": "xhigh" }
    //
    // A file of its own rather than a second field in models.json: a broker
    // built before this reads models.json and would take an object for a
    // broken pick, losing the model too.

    public const string EffortPlaceholder = "{effort}";

    public static string EffortPath(string busRoot) => Path.Combine(busRoot, "cowork", "efforts.json");

    /// <summary>
    /// Lower-case letters only. The level ends up inside codex's
    /// <c>-c model_reasoning_effort=…</c>, which codex parses as TOML: a value
    /// with quotes, commas or brackets could become more config than a level.
    /// </summary>
    private static readonly Regex EffortPattern = new(@"^[a-z]{2,16}$", RegexOptions.CultureInvariant);

    public static bool IsValidEffort(string? effort) => !string.IsNullOrEmpty(effort) && EffortPattern.IsMatch(effort);

    public static string? ReadEffortChoice(string busRoot, string agent)
    {
        var v = ReadEffortChoices(busRoot).TryGetValue(agent, out var e) ? e : null;
        return IsValidEffort(v) ? v : null;
    }

    public static Dictionary<string, string> ReadEffortChoices(string busRoot) => ReadMap(EffortPath(busRoot), IsValidEffort);

    /// <summary>Record an effort pick; null or empty clears it back to the default.</summary>
    public static void WriteEffortChoice(string busRoot, string agent, string? effort)
    {
        if (!string.IsNullOrEmpty(effort) && !IsValidEffort(effort))
            throw new ArgumentException($"'{effort}' is not an effort level", nameof(effort));
        WriteMap(EffortPath(busRoot), agent, effort, IsValidEffort);
    }

    /// <summary>
    /// How a runner with no <c>effortArgs</c> in runners.json is told its
    /// effort: <c>claude --effort high</c>, <c>codex exec -c model_reasoning_effort=high</c>
    /// (no quotes needed — codex takes a value that is not TOML as a string).
    /// Any other CLI: nothing, until runners.json says how.
    /// </summary>
    public static IReadOnlyList<string> DefaultEffortArgs(string agent, string exe) => Vendor(agent, exe) switch
    {
        "claude" => new[] { "--effort", EffortPlaceholder },
        "codex" => new[] { "-c", "model_reasoning_effort=" + EffortPlaceholder },
        _ => Array.Empty<string>(),
    };

    public static List<string> ApplyEffortToArgs(IReadOnlyList<string> args, string? effort, IReadOnlyList<string> effortArgs) =>
        Apply(args, EffortPlaceholder, IsValidEffort(effort) ? effort : null, effortArgs);

    public static bool CanChooseEffort(IReadOnlyList<string> args, IReadOnlyList<string> effortArgs) =>
        args.Concat(effortArgs).Any(a => a.Contains(EffortPlaceholder, StringComparison.Ordinal));

    /// <summary>The levels in words, for both CLIs. An id not named here shows
    /// as itself with the CLI's own description.</summary>
    private static readonly Dictionary<string, (string Label, string Note)> EffortWords = new()
    {
        ["none"] = ("ไม่คิด (none)", "ตอบทันทีไม่ใช้การคิด"),
        ["minimal"] = ("น้อยที่สุด (minimal)", "คิดน้อยที่สุด — เร็วที่สุด"),
        ["low"] = ("ต่ำ (low)", "เร็ว ประหยัด — งานง่าย"),
        ["medium"] = ("กลาง (medium)", "สมดุลระหว่างความเร็วกับความละเอียด — งานทั่วไป"),
        ["high"] = ("สูง (high)", "คิดละเอียดขึ้น — งานซับซ้อน"),
        ["xhigh"] = ("สูงมาก (xhigh)", "คิดลึกมาก — งานยาก ใช้โควตาเร็วขึ้น"),
        ["max"] = ("สูงสุด (max)", "คิดเต็มที่ — อาจใช้ token มากเกินจำเป็น ใช้กับงานที่ยากที่สุดเท่านั้น"),
        ["ultra"] = ("อัลตรา (ultra)", "คิดเต็มที่และแตกงานให้ agent ย่อยเอง — แพงที่สุด"),
    };

    private static Option EffortOption(string id, string? description = null) =>
        EffortWords.TryGetValue(id, out var w) ? new Option(id, w.Label, w.Note) : new Option(id, id, description ?? "");

    private static readonly string[] ClaudeLevels = { "low", "medium", "high", "xhigh", "max" };

    /// <summary>
    /// What Claude Code 2.1.288 itself says each model takes and starts on
    /// (its model table: capabilities "effort" / "xhigh_effort" / "max_effort",
    /// and default_effort). Haiku 4.5 takes no effort at all. A model not
    /// named here is offered every level — the CLI silently lowers one the
    /// model cannot do, so an over-ask costs nothing.
    /// </summary>
    private static readonly Dictionary<string, (string[] Levels, string? Default)> ClaudeEfforts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-fable-5-1"] = (ClaudeLevels, "high"),
            ["claude-opus-5-5"] = (ClaudeLevels, "medium"),
            ["claude-sonnet-5-5"] = (ClaudeLevels, "medium"),
            ["claude-haiku-4-5"] = (Array.Empty<string>(), null),
        };

    /// <summary>
    /// The effort levels a run on <paramref name="model"/> can be started at
    /// (null model = whatever the CLI starts on by itself), lowest first, and
    /// the level it runs at when none is passed (null when that cannot be read).
    ///
    /// runners.json's <c>efforts</c> wins. Then codex's catalogue (each model
    /// lists its own <c>supported_reasoning_levels</c>), then Claude's table.
    /// An empty list means this model takes no effort.
    /// </summary>
    public static IReadOnlyList<Option> SupportedEfforts(string agent, string exe, string? model, JToken? declared,
                                                         out string? cliDefault)
    {
        var vendor = Vendor(agent, exe);
        cliDefault = null;

        if (vendor == "codex")
        {
            var slug = IsValidId(model) ? model! : CodexDefault();
            var entry = slug != null && CodexEffortTable().TryGetValue(slug, out var e) ? e : null;
            cliDefault = CodexDefaultEffort() ?? entry?.Default;
            var mine = ParseDeclared(declared, IsValidEffort);
            if (mine.Count > 0) return mine;
            return entry?.Levels ?? (IReadOnlyList<Option>)Array.Empty<Option>();
        }

        if (vendor == "claude")
        {
            var id = IsValidId(model) ? model! : ClaudeDefault();
            var known = id != null && ClaudeEfforts.TryGetValue(id, out var k) ? k : ((string[] Levels, string? Default)?)null;
            cliDefault = known is { Levels.Length: 0 } ? null : ClaudeDefaultEffort() ?? known?.Default;
            var mine = ParseDeclared(declared, IsValidEffort);
            if (mine.Count > 0) return mine;
            return (known?.Levels ?? ClaudeLevels).Select(l => EffortOption(l)).ToList();
        }

        return ParseDeclared(declared, IsValidEffort);
    }

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

        var mine = ParseDeclared(declared, IsValidId);
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

    private static List<Option> ParseDeclared(JToken? declared, Func<string?, bool> valid)
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
            if (!valid(id) || list.Any(x => x.Id == id)) continue;
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

    private sealed record CodexEffort(IReadOnlyList<Option> Levels, string? Default);

    /// <summary>Each model's <c>supported_reasoning_levels</c> and
    /// <c>default_reasoning_level</c>, from the same catalogue.</summary>
    private static Dictionary<string, CodexEffort> CodexEffortTable()
    {
        var path = Path.Combine(CodexHome(), "models_cache.json");
        return Cached(path, () =>
        {
            var table = new Dictionary<string, CodexEffort>(StringComparer.OrdinalIgnoreCase);
            if (JToken.Parse(File.ReadAllText(path))["models"] is not JArray models) return table;
            foreach (var m in models.OfType<JObject>())
            {
                var id = m["slug"]?.ToString();
                if (!IsValidId(id)) continue;
                var levels = new List<Option>();
                foreach (var l in (m["supported_reasoning_levels"] as JArray ?? new JArray()))
                {
                    var e = l is JObject lo ? lo["effort"]?.ToString() : l.Type == JTokenType.String ? l.ToString() : null;
                    if (IsValidEffort(e) && levels.All(x => x.Id != e))
                        levels.Add(EffortOption(e!, (l as JObject)?["description"]?.ToString()));
                }
                var def = m["default_reasoning_level"]?.ToString();
                table[id!] = new CodexEffort(levels, IsValidEffort(def) ? def : null);
            }
            return table;
        }, "efforts") ?? new Dictionary<string, CodexEffort>();
    }

    /// <summary>The top-level <c>model = "…"</c> of codex's config.toml —
    /// before the first [table], because a profile's model is not the default.</summary>
    private static string? CodexDefault() => CodexTopLevel("model", IsValidId);

    /// <summary>codex's own <c>model_reasoning_effort</c>, which every run
    /// not told otherwise uses whatever the model's default is.</summary>
    private static string? CodexDefaultEffort() => CodexTopLevel("model_reasoning_effort", IsValidEffort);

    private static string? CodexTopLevel(string key, Func<string?, bool> valid)
    {
        var path = Path.Combine(CodexHome(), "config.toml");
        return Cached<string>(path, () =>
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) break;
                var m = Regex.Match(line, @"^" + Regex.Escape(key) + @"\s*=\s*[""']([^""']+)[""']", RegexOptions.CultureInvariant);
                if (m.Success && valid(m.Groups[1].Value)) return m.Groups[1].Value;
            }
            return null;
        }, key);
    }

    /// <summary>Claude Code's <c>model</c> setting, when the owner set one.</summary>
    private static string? ClaudeDefault() => ClaudeSetting("model", IsValidId);

    /// <summary>Claude Code's <c>effortLevel</c> setting, when the owner set one.</summary>
    private static string? ClaudeDefaultEffort() => ClaudeSetting("effortLevel", IsValidEffort);

    private static string? ClaudeSetting(string key, Func<string?, bool> valid)
    {
        var path = Path.Combine(ClaudeHome(), "settings.json");
        return Cached<string>(path, () =>
            JToken.Parse(File.ReadAllText(path))[key]?.ToString() is { } v && valid(v) ? v : null, key);
    }

    private static readonly Dictionary<string, (DateTime Stamp, object? Value)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A value derived from one file, recomputed only when the file
    /// changes. Null when the file is missing or unreadable. <paramref name="what"/>
    /// tells apart two values read from the same file.</summary>
    private static T? Cached<T>(string path, Func<T?> read, string what = "") where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var stamp = File.GetLastWriteTimeUtc(path);
            var key = path + "|" + what;
            lock (_cache)
                if (_cache.TryGetValue(key, out var hit) && hit.Stamp == stamp) return hit.Value as T;
            var value = read();
            lock (_cache) _cache[key] = (stamp, value);
            return value;
        }
        catch { return null; }
    }
}
