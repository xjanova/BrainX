using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services.Chrome;

/// <summary>The Claude extension found in one browser profile.</summary>
public sealed record ChromeExtensionInstall(
    string Browser, string BrowserName, string ProcessName, string ProfileDir, string ProfileName,
    string? ExtensionVersion, bool Enabled);

public sealed class ChromeScanResult
{
    public List<ChromeExtensionInstall> Installs { get; } = new();
    public HashSet<string> RunningBrowsers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool AnyInstalled => Installs.Count > 0;
    public bool AnyEnabled => Installs.Any(i => i.Enabled);
    public bool AnyEnabledRunning => Installs.Any(i => i.Enabled && RunningBrowsers.Contains(i.Browser));
}

/// <summary>
/// Where the Claude extension is installed on this PC, read straight from the
/// browsers' profile folders. Read-only, every file opened with
/// FileShare.ReadWrite so a running browser is never disturbed, and nothing is
/// logged — these folders belong to the owner's browsing.
///
/// What it cannot tell: whether the extension is signed in, with which
/// account, or whether it reached Anthropic's bridge. Only a tool call
/// (list_connected_browsers) answers that, and with the cloud bridge the
/// browser may even be on another computer — so a "missing" or "closed" here
/// is a local hint, not a verdict.
/// </summary>
public static class ChromeExtensionScanner
{
    public sealed class Options
    {
        public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public string AppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        /// <summary>process name → running? Injected by the harness.</summary>
        public Func<string, bool>? IsRunning { get; init; }
    }

    private sealed record BrowserDef(string Id, string Name, string Process, Func<Options, string> Root);

    /// <summary>Same seven browsers, same order, as Claude Code's own detection.
    /// Opera keeps its profile under Roaming, everything else under Local.</summary>
    private static readonly BrowserDef[] Browsers =
    {
        new("chrome",   "Google Chrome", "chrome",   o => Path.Combine(o.LocalAppData, "Google", "Chrome", "User Data")),
        new("brave",    "Brave",         "brave",    o => Path.Combine(o.LocalAppData, "BraveSoftware", "Brave-Browser", "User Data")),
        new("arc",      "Arc",           "Arc",      o => Path.Combine(o.LocalAppData, "Arc", "User Data")),
        new("edge",     "Microsoft Edge","msedge",   o => Path.Combine(o.LocalAppData, "Microsoft", "Edge", "User Data")),
        new("chromium", "Chromium",      "chromium", o => Path.Combine(o.LocalAppData, "Chromium", "User Data")),
        new("vivaldi",  "Vivaldi",       "vivaldi",  o => Path.Combine(o.LocalAppData, "Vivaldi", "User Data")),
        new("opera",    "Opera",         "opera",    o => Path.Combine(o.AppData, "Opera Software", "Opera Stable")),
    };

    public static ChromeScanResult Scan(Options? options = null)
    {
        var o = options ?? new Options();
        var result = new ChromeScanResult();
        var isRunning = o.IsRunning ?? DefaultIsRunning;

        foreach (var b in Browsers)
        {
            string root;
            try { root = b.Root(o); } catch { continue; }
            if (!Directory.Exists(root)) continue;

            var names = ProfileNames(root);
            foreach (var (profileDir, profilePath) in Profiles(root))
            {
                var ext = Path.Combine(profilePath, "Extensions", ChromeBridgeIds.ExtensionId);
                if (!Directory.Exists(ext)) continue;
                var version = HighestVersion(ext);
                var enabled = IsEnabled(profilePath);
                var display = names.TryGetValue(profileDir, out var n) && !string.IsNullOrWhiteSpace(n) ? n : profileDir;
                result.Installs.Add(new ChromeExtensionInstall(b.Id, b.Name, b.Process, profileDir, display, version, enabled));
            }
            try { if (isRunning(b.Process)) result.RunningBrowsers.Add(b.Id); } catch { }
        }
        return result;
    }

    /// <summary>
    /// "Default" and "Profile N". Opera Stable is itself the profile on older
    /// installs (Extensions sits directly under it), so the root counts too.
    /// </summary>
    private static IEnumerable<(string Dir, string Path)> Profiles(string root)
    {
        if (Directory.Exists(Path.Combine(root, "Extensions"))) yield return ("Default", root);
        string[] dirs;
        try { dirs = Directory.GetDirectories(root); } catch { yield break; }
        foreach (var d in dirs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(d);
            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                yield return (name, d);
        }
    }

    /// <summary>profile dir → display name, from "Local State" → profile.info_cache.</summary>
    private static Dictionary<string, string> ProfileNames(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var o = ReadJson(Path.Combine(root, "Local State"));
        if (o?["profile"]?["info_cache"] is JObject cache)
            foreach (var p in cache.Properties())
                if (p.Value is JObject v && v["name"]?.Type == JTokenType.String)
                    map[p.Name] = v["name"]!.ToString();
        return map;
    }

    /// <summary>The newest "&lt;version&gt;_0" folder, by its manifest's version.</summary>
    private static string? HighestVersion(string extDir)
    {
        Version? best = null;
        string? bestText = null;
        string[] dirs;
        try { dirs = Directory.GetDirectories(extDir); } catch { return null; }
        foreach (var d in dirs)
        {
            var text = ReadJson(Path.Combine(d, "manifest.json"))?["version"]?.ToString();
            if (string.IsNullOrWhiteSpace(text))
            {
                var name = Path.GetFileName(d);
                var us = name.LastIndexOf('_');
                text = us > 0 ? name[..us] : name;
            }
            if (!Version.TryParse(text, out var v)) continue;
            if (best == null || v > best) { best = v; bestText = text; }
        }
        return bestText;
    }

    /// <summary>
    /// Enabled unless "Secure Preferences" (falling back to "Preferences")
    /// says otherwise: a non-empty disable_reasons, or the older state == 0.
    /// No entry at all means the browser has not recorded a choice — the
    /// folder is there, so it counts as installed and on.
    /// </summary>
    private static bool IsEnabled(string profilePath)
    {
        foreach (var file in new[] { "Secure Preferences", "Preferences" })
        {
            var s = ReadJson(Path.Combine(profilePath, file))?["extensions"]?["settings"]?[ChromeBridgeIds.ExtensionId];
            if (s is not JObject entry) continue;
            var reasons = entry["disable_reasons"];
            if (reasons is JArray arr && arr.Count > 0) return false;
            if (reasons?.Type == JTokenType.Integer && reasons.Value<long>() != 0) return false;
            if (entry["state"]?.Type == JTokenType.Integer && entry["state"]!.Value<long>() == 0) return false;
            return true;
        }
        return true;
    }

    private static JObject? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return JObject.Parse(sr.ReadToEnd());
        }
        catch { return null; }
    }

    private static bool DefaultIsRunning(string processName)
    {
        var procs = System.Diagnostics.Process.GetProcessesByName(processName);
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }
}
