using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services.Chrome;

public enum ClaudeInstallKind { Override, Desktop, DesktopMsix, Native, Npm, PathExe }

/// <summary>
/// One place Claude Code might live, and how to start it: <see cref="Command"/>
/// plus <see cref="PrefixArgs"/> (the cli.js path for an npm install run under
/// node, nothing for a native exe). The flag goes right after the prefix.
/// </summary>
public sealed record ClaudeCodeCandidate(
    string Path, ClaudeInstallKind Kind, string Command, IReadOnlyList<string> PrefixArgs, string? FolderVersion);

/// <summary>A candidate that passed validation.</summary>
public sealed record ClaudeCodeInstall(
    string Path, ClaudeInstallKind Kind, string Command, IReadOnlyList<string> PrefixArgs, string Version)
{
    /// <summary>The MCP launch: the flag is the first argument the CLI sees.</summary>
    public IReadOnlyList<string> McpArgs => PrefixArgs.Concat(new[] { ChromeBridgeIds.McpFlag }).ToList();

    /// <summary>Index the flag sits at in <see cref="McpArgs"/> — 0 for an exe, 1 for node + cli.js.</summary>
    public int FlagIndex => PrefixArgs.Count;

    /// <summary>A command line a person can paste, for the console actions.</summary>
    public string DisplayCommand(params string[] tail) =>
        string.Join(" ", new[] { Command }.Concat(PrefixArgs).Concat(tail).Select(Quote));

    private static string Quote(string s) => s.Contains(' ') || s.Contains('\\') ? "\"" + s + "\"" : s;
}

public enum ClaudeLocateStatus { Found, NotFound, TooOld, Broken }

public sealed class ClaudeLocateResult
{
    public ClaudeLocateStatus Status { get; init; }
    public ClaudeCodeInstall? Install { get; init; }

    /// <summary>The best candidate that was found but rejected — what the
    /// "too old" / "broken" message names.</summary>
    public ClaudeCodeCandidate? Rejected { get; init; }
    public string? RejectedVersion { get; init; }

    public IReadOnlyList<ClaudeCodeCandidate> Candidates { get; init; } = Array.Empty<ClaudeCodeCandidate>();
}

/// <summary>
/// Finds a Claude Code that can serve <c>--claude-in-chrome-mcp</c>.
///
/// RE-RESOLVED BEFORE EVERY SPAWN AND NEVER PERSISTED. The Claude desktop app
/// installs Claude Code at %APPDATA%\Claude\claude-code\&lt;semver&gt;\&lt;hash&gt;\claude.exe
/// and deletes old version folders on update: on the owner's PC the path a
/// wrapper script still named (2.1.286) was already gone, replaced by 2.1.289
/// and 2.1.293. A remembered path breaks silently on the next update.
///
/// Order: owner override → desktop-bundled (highest version whose folder holds
/// claude.exe AND a .verified marker, real %APPDATA% and the MSIX twin) →
/// ~/.local/bin/claude.exe → npm (node.exe + cli.js — never the .cmd/.ps1/sh
/// shims, which CreateProcess cannot run and whose cmd.exe parent orphans node
/// on kill) → claude.exe on PATH.
///
/// Each candidate is validated with <c>--version</c> (≥ 2.1.0) and by finding
/// the ASCII flag string in the binary, cached by (path, size, mtime). The
/// first that passes wins. 2.0.42 has no flag: started with it, it would run
/// the full CLI and read stdin as a prompt.
/// </summary>
public static class ClaudeCodeLocator
{
    public sealed class Options
    {
        public string AppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        public string LocalAppData { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        public string UserProfile { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        public string ProgramFiles { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        public string PathEnv { get; init; } = Environment.GetEnvironmentVariable("PATH") ?? "";
        public string? Override { get; init; }

        /// <summary>(command, prefixArgs) → raw `--version` stdout, or null.
        /// Injected by the harness so no real claude.exe is ever started there.</summary>
        public Func<string, IReadOnlyList<string>, string?>? VersionProbe { get; init; }

        /// <summary>path → does the binary/script carry the flag string?</summary>
        public Func<string, bool>? FlagProbe { get; init; }
    }

    private static readonly Regex SemVer = new(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.CultureInvariant);
    private static readonly Regex HashDir = new("^[0-9a-f]{8,64}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly ConcurrentDictionary<(string, long, long), (string? Version, bool Flag)> Validated = new();

    /// <summary>
    /// Candidates whose --version did not answer, remembered for a minute: a
    /// binary that hangs would otherwise cost its full 5 s timeout on every
    /// spawn and every bridge_status. Short, because "broken" can be fixed.
    /// </summary>
    private static readonly ConcurrentDictionary<(string, long, long), DateTime> FailedRecently = new();
    private static readonly TimeSpan FailedTtl = TimeSpan.FromMinutes(1);

    /// <summary>Forget validation results — a binary replaced in place with the
    /// same size and mtime is not a real scenario, but a test can need it.</summary>
    public static void ClearCache()
    {
        Validated.Clear();
        FailedRecently.Clear();
    }

    public static ClaudeLocateResult Locate(Options? options = null)
    {
        var o = options ?? new Options();
        var candidates = EnumerateCandidates(o).ToList();
        ClaudeCodeCandidate? tooOld = null, broken = null;
        string? tooOldVersion = null;

        foreach (var c in candidates)
        {
            var (version, hasFlag) = Validate(c, o);
            if (version == null) { broken ??= c; continue; }
            var v = ParseVersion(version);
            if (v == null) { broken ??= c; continue; }
            if (v < ChromeBridgeIds.MinimumVersion || !hasFlag)
            {
                if (tooOld == null) { tooOld = c; tooOldVersion = version; }
                continue;
            }
            return new ClaudeLocateResult
            {
                Status = ClaudeLocateStatus.Found,
                Install = new ClaudeCodeInstall(c.Path, c.Kind, c.Command, c.PrefixArgs, version),
                Candidates = candidates,
            };
        }

        if (tooOld != null)
            return new ClaudeLocateResult { Status = ClaudeLocateStatus.TooOld, Rejected = tooOld, RejectedVersion = tooOldVersion, Candidates = candidates };
        if (broken != null)
            return new ClaudeLocateResult { Status = ClaudeLocateStatus.Broken, Rejected = broken, Candidates = candidates };
        return new ClaudeLocateResult { Status = ClaudeLocateStatus.NotFound, Candidates = candidates };
    }

    /// <summary>Every place to try, best first. Files only — nothing is started.</summary>
    public static IEnumerable<ClaudeCodeCandidate> EnumerateCandidates(Options o)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Fresh(string p) => seen.Add(System.IO.Path.GetFullPath(p));

        // 1. The owner's explicit choice.
        if (!string.IsNullOrWhiteSpace(o.Override) && File.Exists(o.Override))
        {
            var c = FromFile(o.Override!, ClaudeInstallKind.Override, o);
            if (c != null && Fresh(c.Path)) yield return c;
        }

        // 2. Desktop-bundled, real %APPDATA% and the MSIX-virtualised twin,
        //    merged and ordered by version (newest first).
        var desktop = new List<(Version V, ClaudeCodeCandidate C)>();
        desktop.AddRange(DesktopBuilds(System.IO.Path.Combine(o.AppData, "Claude", "claude-code"), ClaudeInstallKind.Desktop));
        foreach (var pkg in SafeDirs(System.IO.Path.Combine(o.LocalAppData, "Packages"), "Claude_*"))
            desktop.AddRange(DesktopBuilds(System.IO.Path.Combine(pkg, "LocalCache", "Roaming", "Claude", "claude-code"),
                                           ClaudeInstallKind.DesktopMsix));
        foreach (var (_, c) in desktop.OrderByDescending(d => d.V))
            if (Fresh(c.Path)) yield return c;

        // 3. The native installer.
        var native = System.IO.Path.Combine(o.UserProfile, ".local", "bin", "claude.exe");
        if (File.Exists(native) && Fresh(native))
            yield return new ClaudeCodeCandidate(native, ClaudeInstallKind.Native, native, Array.Empty<string>(), null);

        // 4. npm global — node.exe + the package's own entry script.
        foreach (var pkgDir in new[]
                 {
                     System.IO.Path.Combine(o.AppData, "npm", "node_modules", "@anthropic-ai", "claude-code"),
                     System.IO.Path.Combine(o.ProgramFiles, "nodejs", "node_modules", "@anthropic-ai", "claude-code"),
                 })
        {
            var c = FromNpmPackage(pkgDir, o);
            if (c != null && Fresh(c.Path)) yield return c;
        }

        // 5. claude.exe on PATH. Only the .exe: claude.cmd / claude.ps1 / the
        //    extensionless sh script are npm shims and are never started.
        foreach (var dir in PathDirs(o.PathEnv))
        {
            string full;
            try { full = System.IO.Path.Combine(dir, "claude.exe"); } catch { continue; }
            if (File.Exists(full) && Fresh(full))
                yield return new ClaudeCodeCandidate(full, ClaudeInstallKind.PathExe, full, Array.Empty<string>(), null);
        }
    }

    /// <summary>
    /// Desktop builds under one claude-code root: per version folder, the
    /// newest hash folder that has BOTH claude.exe and .verified (an update in
    /// progress has the exe but not yet the marker).
    /// </summary>
    private static IEnumerable<(Version, ClaudeCodeCandidate)> DesktopBuilds(string root, ClaudeInstallKind kind)
    {
        foreach (var verDir in SafeDirs(root, "*"))
        {
            var name = System.IO.Path.GetFileName(verDir);
            var v = ParseVersion(name);
            if (v == null) continue;
            var best = SafeDirs(verDir, "*")
                .Where(h => HashDir.IsMatch(System.IO.Path.GetFileName(h)))
                .Where(h => File.Exists(System.IO.Path.Combine(h, "claude.exe")) && File.Exists(System.IO.Path.Combine(h, ".verified")))
                .OrderByDescending(h => SafeMtime(System.IO.Path.Combine(h, "claude.exe")))
                .FirstOrDefault();
            if (best == null) continue;
            var exe = System.IO.Path.Combine(best, "claude.exe");
            yield return (v, new ClaudeCodeCandidate(exe, kind, exe, Array.Empty<string>(), name));
        }
    }

    private static ClaudeCodeCandidate? FromFile(string path, ClaudeInstallKind kind, Options o)
    {
        var ext = System.IO.Path.GetExtension(path);
        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new ClaudeCodeCandidate(path, kind, path, Array.Empty<string>(), null);
        if (ext.Equals(".js", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            var node = FindNode(o);
            return node == null ? null : new ClaudeCodeCandidate(path, kind, node, new[] { path }, null);
        }
        return null;   // .cmd / .ps1 / .bat / a shell script — never
    }

    private static ClaudeCodeCandidate? FromNpmPackage(string pkgDir, Options o)
    {
        try
        {
            var pj = System.IO.Path.Combine(pkgDir, "package.json");
            if (!File.Exists(pj)) return null;
            var root = JObject.Parse(File.ReadAllText(pj));
            var version = root["version"]?.ToString();
            string? rel = root["bin"] switch
            {
                JValue s => s.ToString(),
                JObject bins => bins["claude"]?.ToString() ?? bins.Properties().FirstOrDefault()?.Value?.ToString(),
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(rel)) rel = "cli.js";
            var entry = System.IO.Path.GetFullPath(System.IO.Path.Combine(pkgDir, rel));
            // The entry must stay inside the package: a package.json is data.
            if (!entry.StartsWith(System.IO.Path.GetFullPath(pkgDir), StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(entry)) return null;
            var c = FromFile(entry, ClaudeInstallKind.Npm, o);
            return c == null ? null : c with { FolderVersion = version };
        }
        catch { return null; }
    }

    private static string? FindNode(Options o)
    {
        foreach (var dir in PathDirs(o.PathEnv))
        {
            try
            {
                var p = System.IO.Path.Combine(dir, "node.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        var pf = System.IO.Path.Combine(o.ProgramFiles, "nodejs", "node.exe");
        return File.Exists(pf) ? pf : null;
    }

    // ───────────── validation ─────────────

    private static (string? Version, bool Flag) Validate(ClaudeCodeCandidate c, Options o)
    {
        long size, mtime;
        try
        {
            var fi = new FileInfo(c.Path);
            size = fi.Length;
            mtime = fi.LastWriteTimeUtc.Ticks;
        }
        catch { return (null, false); }

        var key = (System.IO.Path.GetFullPath(c.Path).ToLowerInvariant(), size, mtime);
        // Cached only when injected probes are NOT in play: a test that swaps
        // probes between runs must not read the previous run's answer.
        var cacheable = o.VersionProbe == null && o.FlagProbe == null;
        if (cacheable && Validated.TryGetValue(key, out var hit)) return hit;
        if (cacheable && FailedRecently.TryGetValue(key, out var failedAt) && DateTime.UtcNow - failedAt < FailedTtl)
            return (null, false);

        var raw = (o.VersionProbe ?? DefaultVersionProbe)(c.Command, c.PrefixArgs);
        var version = raw == null ? null : SemVer.Match(raw) is { Success: true } m ? m.Value : null;
        var flag = version != null && (o.FlagProbe ?? ContainsFlag)(c.Path);
        var result = (version, flag);
        if (cacheable && version != null) Validated[key] = result;
        else if (cacheable) FailedRecently[key] = DateTime.UtcNow;
        return result;
    }

    private static string? DefaultVersionProbe(string command, IReadOnlyList<string> prefix)
    {
        var r = ClaudeChildEnv.RunProbe(command, prefix.Concat(new[] { "--version" }), TimeSpan.FromSeconds(5));
        return r is { } ok && ok.ExitCode == 0 ? ok.Stdout : null;
    }

    /// <summary>
    /// Does the file carry the ASCII flag? Streamed in 1 MB blocks with an
    /// overlap, so a 250 MB claude.exe costs a sequential read and no more
    /// than one block of memory.
    /// </summary>
    public static bool ContainsFlag(string path)
    {
        var needle = Encoding.ASCII.GetBytes(ChromeBridgeIds.McpFlag);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            var buf = new byte[(1 << 20) + needle.Length];
            var carry = 0;
            int read;
            while ((read = fs.Read(buf, carry, buf.Length - carry)) > 0)
            {
                var span = new ReadOnlySpan<byte>(buf, 0, carry + read);
                if (span.IndexOf(needle) >= 0) return true;
                carry = Math.Min(needle.Length - 1, span.Length);
                span[^carry..].CopyTo(buf);
            }
        }
        catch { }
        return false;
    }

    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = SemVer.Match(s);
        if (!m.Success) return null;
        try { return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value)); }
        catch { return null; }
    }

    private static IEnumerable<string> SafeDirs(string root, string pattern)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> PathDirs(string pathEnv) =>
        pathEnv.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(d => d.Trim('"'))
               .Where(d => d.Length > 0 && System.IO.Path.IsPathRooted(d));

    private static long SafeMtime(string p)
    {
        try { return File.GetLastWriteTimeUtc(p).Ticks; } catch { return 0; }
    }
}
