using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Can BrainX actually reach this agent — is its program on this machine, and signed in where that
/// is checkable? And if not, what does the owner do about it?
///
/// Owner (2026-10-17): "ถ้าติดต่อใครไม่ได้ก็อย่าให้เลือกโมเดลได้ เช่น ติดต่อ grok ไม่ได้ ซึ่งอาจไม่ได้
/// ลงโปรแกรมไว้ … และไม่แสดงชื่อใน agent bus … แต่ต้องบอกให้ผู้ใช้ทราบว่าต้องทำไง อาจมีลิ้งค์ให้ไปโหลด".
/// The room's model picker and the dashboard's agent-bus card both ask here, so they agree.
///
/// Resolution mirrors the broker's own (BrainX.Mcp Program.Broker ResolveRunnerExe: exe, then
/// exeFallbacks; each a bare name on PATH, a path, or a `*` pattern expanded segment by segment),
/// so "installed" here means "the broker would find it".
/// </summary>
public static class AgentInstall
{
    public sealed record Reach(bool Installed, bool Ready, string Fix, string? Url);

    /// <summary>Where each agent comes from. The room opens only these addresses (KnownUrls).</summary>
    private static readonly Dictionary<string, (string Install, string Url)> Guides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = ("ติดตั้ง Claude desktop (มี Claude Code ในตัว) แล้วเปิดแอปให้ล็อกอินหนึ่งครั้ง", "https://claude.com/download"),
        ["codex"] = ("ติดตั้งแอป Codex ของ OpenAI แล้วล็อกอิน", "https://openai.com/codex/"),
        ["grok"] = ("ติดตั้ง Grok Build (xAI CLI) แล้วพิมพ์ grok ในเทอร์มินัลเพื่อล็อกอิน", "https://x.ai/cli"),
        ["gemini"] = ("ติดตั้ง Node.js แล้วพิมพ์ npm install -g @google/gemini-cli ในเทอร์มินัล", "https://github.com/google-gemini/gemini-cli"),
    };
    private const string GeminiKeyFix = "ใส่ Gemini API key ที่ BrainX → ตั้งค่า → Google Gemini";

    public static readonly IReadOnlySet<string> KnownUrls =
        Guides.Values.Select(g => g.Url).Append(GoogleAiKey.GetKeyUrl).ToHashSet(StringComparer.Ordinal);

    /// <summary>How the owner gets this agent, or a generic line for one BrainX has no guide for.</summary>
    public static (string Fix, string? Url) GuideFor(string agent) =>
        Guides.TryGetValue(agent, out var g) ? (g.Install, g.Url) : ($"ติดตั้งโปรแกรมของ {agent} ตามที่ runners.json ระบุ", null);

    /// <summary>Probe one runners.json entry.</summary>
    public static Reach Probe(string agent, JObject runner)
    {
        var exe = runner["exe"]?.ToString() ?? "";
        var fallbacks = runner["exeFallbacks"] switch
        {
            JArray a => a.Select(t => t.ToString()).ToList(),
            JValue v when v.Type == JTokenType.String => new List<string> { v.ToString() },
            _ => new List<string>(),
        };
        var found = Resolve(exe, fallbacks);
        // A node runner (Gemini CLI) is only there when its script is: node alone proves nothing.
        if (found != null && Path.GetFileNameWithoutExtension(found).Equals("node", StringComparison.OrdinalIgnoreCase)
            && (runner["args"] as JArray)?.Select(t => Environment.ExpandEnvironmentVariables(t.ToString()))
                .FirstOrDefault(a => a.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)) is { } script
            && Path.IsPathRooted(script) && !File.Exists(script))
            found = null;

        var (fix, url) = GuideFor(agent);
        if (found == null) return new Reach(false, false, fix, url);
        if (agent.Equals("gemini", StringComparison.OrdinalIgnoreCase) && !GeminiSignedIn())
            return new Reach(true, false, GeminiKeyFix, GoogleAiKey.GetKeyUrl);
        return new Reach(true, true, "", null);
    }

    /// <summary>Gemini CLI headless needs a key (Settings or environment) or a Google login it saved.</summary>
    public static bool GeminiSignedIn() =>
        GoogleAiKey.Read() != null
        || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json"));

    public static string? Resolve(string exe, IEnumerable<string> fallbacks)
    {
        foreach (var raw in fallbacks.Prepend(exe))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var spec = Environment.ExpandEnvironmentVariables(raw.Trim());
            if (spec.Contains('*')) { if (NewestMatch(spec) is { } m) return m; continue; }
            if (Path.IsPathRooted(spec)) { if (File.Exists(spec)) return spec; continue; }
            if (OnPath(spec) is { } p) return p;
        }
        return null;
    }

    private static string? NewestMatch(string pattern)
    {
        try
        {
            var parts = pattern.Split(Path.DirectorySeparatorChar);
            var star = Array.FindIndex(parts, p => p.Contains('*'));
            var level = new List<string> { string.Join(Path.DirectorySeparatorChar, parts[..star]) + Path.DirectorySeparatorChar };
            for (var i = star; i < parts.Length; i++)
            {
                var last = i == parts.Length - 1;
                var next = new List<string>();
                foreach (var dir in level)
                {
                    if (!Directory.Exists(dir)) continue;
                    if (!parts[i].Contains('*'))
                    {
                        var p = Path.Combine(dir, parts[i]);
                        if (last ? File.Exists(p) : Directory.Exists(p)) next.Add(p);
                        continue;
                    }
                    next.AddRange(last ? Directory.GetFiles(dir, parts[i]) : Directory.GetDirectories(dir, parts[i]));
                }
                level = next;
                if (level.Count == 0) return null;
            }
            return level.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>PATH as the user has it NOW: the process's own copy goes stale when something is
    /// installed after BrainX started, so the user and machine values are read fresh too.</summary>
    private static string? OnPath(string exe)
    {
        var dirs = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        }.Where(s => !string.IsNullOrEmpty(s)).SelectMany(s => s!.Split(Path.PathSeparator)).Distinct(StringComparer.OrdinalIgnoreCase);
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : new[] { "" };
        foreach (var dir in dirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in exts)
            {
                try
                {
                    var c = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim()), exe + ext.ToLowerInvariant());
                    if (File.Exists(c)) return c;
                }
                catch { }
            }
        }
        return null;
    }
}
