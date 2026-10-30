using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using BrainX.Core.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Mcp;

/// <summary>
/// `brainx-mcp install-hooks` — register the native Claude Code hooks in
/// ~/.claude/settings.json and write the two files that go with them.
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
///  - Every entry this writes carries <see cref="NativeTag"/>, so a re-run
///    REPLACES its own entries and touches nothing else. Idempotent.
///  - The file is written WITHOUT a BOM. Claude Code parses it with a JSON
///    parser that rejects one, and a BOM here has bricked a config before.
///  - A timestamped backup is written first, and the merged document must
///    re-parse before it replaces the original.
///  - Every hook runs in the binary (NativeHooks.cs), in exec form: `command` is
///    the exe, `args` its arguments,
///    no `shell`. Exec form is not a style choice. Powershell.exe alone took 3 to
///    5.5 s here and Git Bash 0.3 to 0.4 s, per hook, per tool call; with `args`
///    Claude Code starts the exe directly. It also cannot repeat 2026-09-25: there
///    is no shell field to lose and no shell to mangle a path.
/// </summary>
internal static partial class Program
{
    /// <summary>The resolved vault, for callers outside this partial (CliInstall).</summary>
    internal static string VaultPath => _vaultPath;

    /// <summary>Bumped when the SET of hooks changes.
    /// v3: five of them moved from PowerShell into the binary.
    /// v4: the last two did, so no hook starts PowerShell.</summary>
    private const string HooksVersion = "v4";

    /// <summary>Stamped on every entry this installer writes: how a re-run finds its
    /// own among the user's. A real argument, which the binary ignores, because
    /// exec form has no shell to make a trailing `#` a comment.</summary>
    private const string NativeTag = "brainx-hooks-" + HooksVersion;

    private sealed record HookSpec(string Event, string? Matcher, string Native, int TimeoutSec);

    /// <summary>
    /// The wiring, in one place. Matchers are deliberately narrow: PreToolUse
    /// and the error-recall PostToolUse run on the critical path of every
    /// matching tool call, so they must not be asked about tools they would
    /// only exit on.
    /// </summary>
    private static readonly HookSpec[] Hooks =
    {
        new("SessionStart",     null,                    "hook-brain-start",  15),
        new("UserPromptSubmit", null,                    "hook-prompt-gate",  15),
        new("PreToolUse",       "Edit|Write|MultiEdit",  "hook-pretool-warn", 10),
        new("PostToolUse",      ".*",                    "hook-tool-log",     10),
        new("PostToolUse",      "Bash|PowerShell",       "hook-error-recall", 10),
        new("Stop",             null,                    "hook-brain-end",    15),
        // v2: a session that closes, or loses its context to compaction, without
        // a #session-handoff note leaves an auto digest instead of nothing.
        new("SessionEnd",       null,                    "hook-session-end",  30),
        new("PreCompact",       null,                    "hook-session-end",  30),
    };

    /// <summary>
    /// The scripts the table above replaced. Needed by the filename arm of
    /// <see cref="IsOurs"/>: an entry wired by hand before this installer existed
    /// carries no marker, and if it survived the upgrade every native hook would
    /// run beside the PowerShell it replaced. The files stay on disk, unused.
    /// </summary>
    private static readonly string[] RetiredScripts =
    {
        "brain-prompt-gate.ps1", "brain-pretool-warn.ps1", "brain-tool-logger.ps1",
        "brain-error-recall.ps1", "stop-hook.ps1", "session-start.ps1", "session-end.ps1",
    };

    /// <summary>Shipped alongside the hooks but not registered: the diagnostic the
    /// install summary points at, and the aliases hook-prompt-gate reads.</summary>
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
                Console.WriteLine("Registers the native `brainx-mcp hook-*` hooks in ~/.claude/settings.json (merged,");
                Console.WriteLine("never overwritten) and writes brain-stats.ps1 and brain-aliases.json to");
                Console.WriteLine("~/.claude/scripts. Re-running replaces only this installer's own entries.");
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

        // ── 1. support files ──────────────────────────────────────────────
        var asm = Assembly.GetExecutingAssembly();
        var noBom = new UTF8Encoding(false);
        int written = 0, unchanged = 0;

        if (!dryRun) Directory.CreateDirectory(scriptsDir);
        foreach (var file in SupportFiles)
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

        // The binary a native hook names. Only a real brainx-mcp will do: under
        // `dotnet brainx-mcp.dll` ProcessPath is dotnet.exe, and every hook would
        // then run the bare .NET host.
        var self = Environment.ProcessPath ?? "";
        if (!Path.GetFileNameWithoutExtension(self).Equals("brainx-mcp", StringComparison.OrdinalIgnoreCase))
            self = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "brainx-mcp.exe" : "brainx-mcp");
        var hookExe = McpRuntime.HookExe(self, dryRun, Say);
        if (!File.Exists(hookExe))
        {
            Console.Error.WriteLine($"  ✗ no brainx-mcp binary to register at {hookExe}.");
            return 2;
        }
        var execForm = HookHealth.ExecFormSupported();
        Say($"  binary: {hookExe}{(execForm ? "" : "  (shell form: this Claude Code predates exec form)")}");
        var vaultArg = vaultPath.TrimEnd('\\', '/');

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
                entry["hooks"] = new JArray(NativeHandler(hookExe, h.Native, vaultArg, NativeTag, null, h.TimeoutSec, execForm));
                arr.Add(entry);
                added++;
            }
        }

        // The auto-ingest entry is BrainX.Client's: it holds the owner's opt-out,
        // so it is upgraded where it stands and never added. It still has to move,
        // because Claude Code runs matching hooks in parallel and waits for the
        // slowest: one PowerShell here and every Read and Edit stays seconds long.
        var ingestUrl = AutoIngestUrl(scriptsDir);
        foreach (var handler in (hooksNode["PostToolUse"] as JArray ?? new JArray())
                     .SelectMany(g => g["hooks"] as JArray ?? new JArray()).OfType<JObject>().ToList())
        {
            if (!HookHealth.IsAutoIngestHook(HookHealth.CommandText(handler))) continue;
            handler.Replace(NativeHandler(hookExe, "hook-auto-ingest", vaultArg, HookHealth.AutoIngestNativeTag, ingestUrl,
                                          handler["timeout"]?.ToObject<int?>() ?? 10, execForm));
            Say("  auto-ingest: upgraded to native");
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
    /// A hook handler that runs this binary. Exec form unless Claude Code is too
    /// old for it; shell form then carries no "shell" field, because a quoted
    /// path at the start of a PowerShell line is a string, not a command.
    /// </summary>
    private static JObject NativeHandler(string exe, string subcommand, string vault, string tag, string? ingestUrl, int timeoutSec, bool execForm)
    {
        var args = new List<string> { subcommand, "--vault", vault };
        if (ingestUrl != null) { args.Add("--ingest-url"); args.Add(ingestUrl); }
        args.Add("--tag");
        args.Add(tag);

        var handler = new JObject { ["type"] = "command" };
        if (execForm)
        {
            handler["command"] = exe;
            handler["args"] = new JArray(args);
        }
        else
        {
            // Forward slashes: they mean the same to bash, cmd and PowerShell, and a
            // backslash before a closing quote would escape it in bash. Git Bash
            // mangling a path took every hook out for two weeks from 2026-09-25.
            var quoted = args.Select(a => a.Contains(' ') || a.Contains('\\') || a.Contains(':') ? $"\"{a.Replace('\\', '/')}\"" : a);
            handler["command"] = $"\"{exe.Replace('\\', '/')}\" {string.Join(" ", quoted)}";
        }
        handler["timeout"] = timeoutSec;
        return handler;
    }

    /// <summary>The ingest URL the client baked into its script, so a machine on
    /// another port keeps it. The default is the port the client ships with.</summary>
    private static string AutoIngestUrl(string scriptsDir)
    {
        try
        {
            var m = Regex.Match(File.ReadAllText(Path.Combine(scriptsDir, "brain-auto-ingest.ps1")), "-Uri '([^']+)'");
            if (m.Success) return m.Groups[1].Value.Replace("''", "'");
        }
        catch { }
        return "http://localhost:5142/api/brain/auto-ingest";
    }

    /// <summary>
    /// True when this array element is one of ours — by a marker, or by naming
    /// one of the scripts the hooks used to be.
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
            var cmd = HookHealth.CommandText(h);
            if (cmd.Length == 0) return false;
            if (HookHealth.IsInstallerHook(cmd)) return true;
            return RetiredScripts.Any(script => cmd.Contains(script, StringComparison.OrdinalIgnoreCase));
        });
    }
}
