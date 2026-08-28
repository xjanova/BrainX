using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

/// <summary>
/// `brainx-mcp install-hooks` — write the Claude Code hook scripts and
/// register them in ~/.claude/settings.json.
///
/// WHY THIS EXISTS. Until 2026-08-28 these scripts lived only in one
/// developer's ~/.claude/scripts and in no repository at all. Three
/// consequences, all of them real:
///
///  1. The `push-pack` builder shipped in v2.0.303 with no consumer. Every
///     other machine rebuilt files.tsv/errors.tsv nightly and nothing ever
///     read them — a half-open loop that reports success.
///  2. A rebrand (obsidianx-brain -> brainx-brain) broke a tool-name regex in
///     two of these scripts and it went unnoticed for THREE MONTHS. A file
///     outside version control has no diff, no review and no CI; that is most
///     of why it survived.
///  3. One disk failure would have taken the whole hook layer with it.
///
/// DESIGN RULES, each paid for by a past incident:
///  - settings.json is MERGED, never rewritten. The user owns that file and it
///    holds unrelated hooks, plugins and preferences.
///  - Every entry this writes carries <see cref="Marker"/> in its command, so a
///    re-run REPLACES its own entries and touches nothing else. Idempotent.
///  - The file is written WITHOUT a BOM. Claude Code parses it with a JSON
///    parser that rejects one, and a BOM here has bricked a config before.
///  - A timestamped backup is written first, and the merged document must
///    re-parse before it replaces the original.
/// </summary>
internal static partial class Program
{
    /// <summary>The resolved vault, for callers outside this partial (CliInstall).</summary>
    internal static string VaultPath => _vaultPath;

    /// <summary>Stamped into every command line this installer writes. It is
    /// how a re-run finds its own entries among the user's.</summary>
    private const string Marker = "# brainx-hooks";

    /// <summary>Bumped when the SET of hooks changes (not when a script's body
    /// changes — that is handled by overwriting the script file).</summary>
    private const string HooksVersion = "v1";

    private sealed record HookSpec(string Event, string? Matcher, string Script, int TimeoutSec);

    /// <summary>
    /// The wiring, in one place. Matchers are deliberately narrow: PreToolUse
    /// and the error-recall PostToolUse run on the critical path of every
    /// matching tool call, so they must not be asked about tools they would
    /// only exit on.
    /// </summary>
    private static readonly HookSpec[] Hooks =
    {
        new("SessionStart",     null,                    "session-start.ps1",      15),
        new("UserPromptSubmit", null,                    "brain-prompt-gate.ps1",  15),
        new("PreToolUse",       "Edit|Write|MultiEdit",  "brain-pretool-warn.ps1", 10),
        new("PostToolUse",      ".*",                    "brain-tool-logger.ps1",  10),
        new("PostToolUse",      "Bash|PowerShell",       "brain-error-recall.ps1", 10),
        new("Stop",             null,                    "stop-hook.ps1",          15),
    };

    /// <summary>Shipped alongside the hooks but not registered: called BY
    /// session-start.ps1, and a diagnostic the install summary points at.</summary>
    private static readonly string[] SupportFiles = { "brain-stats.ps1", "brain-aliases.json" };

    internal static int InstallHooksCli(string[] args)
    {
        string? vaultArg = null;
        bool dryRun = false, quiet = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--vault" && i + 1 < args.Length) vaultArg = args[++i];
            else if (args[i] == "--dry-run") dryRun = true;
            else if (args[i] == "--quiet") quiet = true;
            else if (args[i] is "-h" or "--help" or "help")
            {
                Console.WriteLine("Usage: brainx-mcp install-hooks [--vault PATH] [--dry-run] [--quiet]");
                Console.WriteLine();
                Console.WriteLine("Writes the brain-first hook scripts to ~/.claude/scripts and registers");
                Console.WriteLine("them in ~/.claude/settings.json (merged, never overwritten). Re-running");
                Console.WriteLine("updates the scripts and replaces only this installer's own entries.");
                Console.WriteLine();
                Console.WriteLine("  --dry-run   show what would change; write nothing");
                return 0;
            }
        }
        if (!string.IsNullOrWhiteSpace(vaultArg) && Directory.Exists(vaultArg))
            _vaultPath = Path.GetFullPath(vaultArg);

        void Say(string s) { if (!quiet) Console.WriteLine(s); }
        Say($"brainx-mcp install-hooks · v{ServerVersion}{(dryRun ? "  [DRY RUN]" : "")}");
        Say($"  vault:  {_vaultPath}");

        try
        {
            return InstallHooks(_vaultPath, dryRun, Say);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"install-hooks failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    internal static int InstallHooks(string vaultPath, bool dryRun, Action<string>? say = null)
    {
        void Say(string s) => say?.Invoke(s);

        // CLAUDE_CONFIG_DIR is Claude Code's own override for where its config
        // lives. Honouring it is correct on its merits — a machine that sets it
        // keeps settings.json somewhere else and writing to ~/.claude there
        // would install hooks nothing ever loads — and it doubles as the seam
        // that lets this be tested against a sandbox instead of a live config.
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var claudeDir = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        var scriptsDir = Path.Combine(claudeDir, "scripts");
        Say($"  target: {scriptsDir}");

        // ── 1. scripts ────────────────────────────────────────────────────
        var asm = Assembly.GetExecutingAssembly();
        var wanted = Hooks.Select(h => h.Script).Concat(SupportFiles).Distinct().ToList();
        var noBom = new UTF8Encoding(false);
        int written = 0, unchanged = 0;

        if (!dryRun) Directory.CreateDirectory(scriptsDir);
        foreach (var file in wanted)
        {
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + file, StringComparison.OrdinalIgnoreCase));
            if (resName == null)
            {
                Console.Error.WriteLine($"  ✗ {file}: not embedded in this binary — build is incomplete");
                return 2;
            }
            using var stream = asm.GetManifestResourceStream(resName)!;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var body = reader.ReadToEnd();

            // The scripts prefer $env:BRAINX_VAULT and fall back to this token.
            // Substituting at install time means a machine with no env var set
            // still resolves the right vault.
            body = body.Replace("__BRAINX_VAULT__", vaultPath.TrimEnd('\\', '/'));

            var dest = Path.Combine(scriptsDir, file);
            // A .ps1 carrying non-ASCII MUST keep a BOM: Windows PowerShell 5.1
            // decodes a BOM-less file as the machine's ANSI codepage and an
            // em-dash then becomes a parser error that kills the whole script.
            // Detected on content, not on the source file, so a hand-edit that
            // introduces Thai text is still written correctly.
            var needsBom = file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                           && body.Any(c => c > 127);
            // GetBytes never emits the preamble whatever the encoding was
            // constructed with, so the BOM is prepended explicitly or not at all.
            var bytes = noBom.GetBytes(body);
            if (needsBom) bytes = Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();

            if (File.Exists(dest) && File.ReadAllBytes(dest).SequenceEqual(bytes)) { unchanged++; continue; }
            if (!dryRun) File.WriteAllBytes(dest, bytes);
            written++;
            Say($"    {(dryRun ? "would write" : "wrote")}  {file}{(needsBom ? "  (BOM)" : "")}");
        }
        Say($"  scripts: {written} written, {unchanged} already current");

        // ── 2. settings.json ──────────────────────────────────────────────
        var settingsPath = Path.Combine(claudeDir, "settings.json");
        JObject root;
        if (File.Exists(settingsPath))
        {
            var text = File.ReadAllText(settingsPath);            // strips a BOM if present
            try { root = JObject.Parse(text); }
            catch (JsonException ex)
            {
                Console.Error.WriteLine($"  ✗ {settingsPath} is not valid JSON ({ex.Message}).");
                Console.Error.WriteLine("    Refusing to touch it — fix or move it, then re-run.");
                return 2;
            }
        }
        else root = new JObject();

        var hooksNode = root["hooks"] as JObject;
        if (hooksNode == null) { hooksNode = new JObject(); root["hooks"] = hooksNode; }

        int added = 0, replaced = 0, kept = 0;
        foreach (var group in Hooks.GroupBy(h => h.Event))
        {
            var arr = hooksNode[group.Key] as JArray;
            if (arr == null) { arr = new JArray(); hooksNode[group.Key] = arr; }

            // Drop only OUR entries; everything else in this event's array is
            // the user's and survives untouched.
            var mine = arr.Where(IsOurs).ToList();
            foreach (var m in mine) { m.Remove(); replaced++; }
            kept += arr.Count;

            foreach (var h in group)
            {
                var entry = new JObject();
                if (h.Matcher != null) entry["matcher"] = h.Matcher;
                entry["hooks"] = new JArray(new JObject
                {
                    ["type"] = "command",
                    ["command"] = $"powershell -NoProfile -ExecutionPolicy Bypass -File "
                                + $"\"$env:USERPROFILE\\.claude\\scripts\\{h.Script}\" {Marker} {HooksVersion}",
                    ["shell"] = "powershell",
                    ["timeout"] = h.TimeoutSec,
                });
                arr.Add(entry);
                added++;
            }
        }

        Say($"  settings: {added} entr(ies) registered"
          + (replaced > 0 ? $", {replaced} previous brainx entr(ies) replaced" : "")
          + $", {kept} unrelated entr(ies) untouched");

        if (dryRun) { Say("  [dry run] settings.json not written"); return 0; }

        // Backup, then verify the merged document re-parses before it lands.
        if (File.Exists(settingsPath))
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            File.Copy(settingsPath, $"{settingsPath}.bak-{stamp}", overwrite: true);
            Say($"  backup:  settings.json.bak-{stamp}");
        }
        var merged = root.ToString(Formatting.Indented);
        try { JObject.Parse(merged); }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"  ✗ merged settings would not re-parse ({ex.Message}) — nothing written.");
            return 2;
        }
        // No BOM: Claude Code's JSON parser rejects one, and writing config
        // with `Set-Content -Encoding utf8` has bricked this file before.
        File.WriteAllText(settingsPath, merged, noBom);
        Say($"  wrote    {settingsPath}");

        Say("");
        Say("  Hooks take effect in the NEXT Claude Code session (settings load at start).");
        Say("  Verify: start a session and look for a REPO PACK / SESSION RESUME block.");
        return 0;
    }

    /// <summary>
    /// True when this array element is one of ours — by <see cref="Marker"/>,
    /// or by naming one of our scripts.
    ///
    /// The filename arm is the MIGRATION path and it is not optional: every
    /// machine that wired these hooks by hand before this installer existed
    /// (including the one they were developed on) has unmarked entries. Without
    /// it the first run appends a second copy of all six, every one of them
    /// fires, and the duplicate SessionStart injection looks like a brain bug
    /// rather than an install bug.
    /// </summary>
    private static bool IsOurs(JToken entry)
    {
        if (entry is not JObject o || o["hooks"] is not JArray hs) return false;
        return hs.Any(h =>
        {
            var cmd = h?["command"]?.ToString();
            if (string.IsNullOrEmpty(cmd)) return false;
            if (cmd.Contains(Marker, StringComparison.Ordinal)) return true;
            return Hooks.Any(spec => cmd.Contains(spec.Script, StringComparison.OrdinalIgnoreCase));
        });
    }
}
