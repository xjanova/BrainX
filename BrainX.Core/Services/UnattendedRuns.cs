using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BrainX.Core.Services.Chrome;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// How a brain knows it is serving a run nobody is watching — a task the
/// broker started for queued mail, a card or the cowork room — and so must
/// not hand that run the owner's browser (chrome__*), the private browsing
/// log, or the agent's cowork seat.
///
/// Two independent signals, because neither alone survives every runner:
///
///  1. THE ENVIRONMENT. The broker sets BRAINX_BROKER_RUN=1 on the runner.
///     Claude Code hands its stdio MCP servers its whole environment, and the
///     Gemini CLI hands them everything its secret-name filter keeps (the name
///     has no TOKEN/KEY/AUTH/… in it). Codex does NOT: it clears the
///     environment of every MCP server down to a fixed allow-list (PATH,
///     SYSTEMROOT, USERPROFILE, LOCALAPPDATA, TEMP…) plus the server's own
///     configured `env` — so for codex the variable goes in as a config
///     override on the server's env table (<see cref="ForRunner"/>), the way
///     the Mind's runner already did. Review 2026-10-10 (rev3): the default
///     codex runner had no such override, and a peer-fed codex task listed
///     and called chrome__* with the owner's switch on.
///
///  2. THE PROCESS TREE. The broker records every runner it starts —
///     pid AND creation time — in a local file outside the vault
///     (<see cref="Register(int, string, string?)"/>). A brain whose ancestor
///     is a recorded runner is in an unattended run whatever its environment
///     says (<see cref="UnderRegisteredRun"/>). This covers a runner whose CLI
///     filters the environment in a way nobody has read yet (Grok Build, a
///     custom one in runners.json) and a codex whose brain server is
///     registered under a name the override cannot see.
///
/// Fail direction: a missed signal leaves a run treated as attended (the old
/// behaviour); a false one turns the browser off for that run. The creation
/// time makes a false one by pid reuse impossible: a recorded entry matches
/// only the very process it was written for.
/// </summary>
public static class UnattendedRuns
{
    public const string BrokerRunEnv = "BRAINX_BROKER_RUN";
    public const string AsAgentEnv = "BRAINX_AS_AGENT";

    /// <summary>codex's MCP server table, as its `-c key=value` overrides name it.</summary>
    private const string CodexServersKey = "mcp_servers";

    // ───────────── 1. what a runner is started with ─────────────

    /// <summary>What a broker runner is launched with, beyond its runners.json args.</summary>
    public sealed record Launch(List<string> Args, Dictionary<string, string> Env, IReadOnlyList<string> CodexServers);

    /// <summary>
    /// The runner's args and the environment entries that tell the brain it
    /// talks to that this is a broker run.
    ///
    /// Every runner gets BRAINX_BROKER_RUN=1 in its environment (what claude
    /// and gemini pass on). A codex runner additionally gets, for every
    /// brain server in its config.toml, <c>-c mcp_servers.&lt;name&gt;.env.BRAINX_BROKER_RUN="1"</c>
    /// — only for servers that exist there with a `command` (an override on a
    /// missing server would leave codex a server with no transport, and codex
    /// refuses a config like that outright; on a url server `env` is refused
    /// the same way). Placed after the leading subcommand, like the model flag.
    /// An override already in the args (the Mind's) is not repeated.
    /// </summary>
    /// <param name="exe">The resolved runner executable.</param>
    /// <param name="args">The runner's args, before {prompt}/{cwd} substitution.</param>
    /// <param name="codexConfigToml">codex's config.toml text (null = none / unreadable). Read only for a codex runner.</param>
    public static Launch ForRunner(string exe, IReadOnlyList<string> args, string? codexConfigToml)
    {
        var result = args.ToList();
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [BrokerRunEnv] = "1" };
        if (!IsCodex(exe)) return new Launch(result, env, Array.Empty<string>());

        var servers = BrainServersInCodexConfig(codexConfigToml);
        var insert = new List<string>();
        foreach (var name in servers)
        {
            var value = $"{CodexServersKey}.{name}.env.{BrokerRunEnv}=\"1\"";
            if (result.Any(a => string.Equals(a, value, StringComparison.Ordinal))) continue;
            insert.Add("-c");
            insert.Add(value);
        }
        if (insert.Count > 0)
        {
            var at = result.FindIndex(a => a.StartsWith('-') || a.Contains("{prompt}", StringComparison.Ordinal));
            if (at < 0) at = result.Count;
            result.InsertRange(at, insert);
        }
        return new Launch(result, env, servers);
    }

    /// <summary>codex, by its file name: codex.exe, the npm shim codex.cmd, or a vendored codex-&lt;triple&gt;.exe.</summary>
    public static bool IsCodex(string? exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe ?? "").ToLowerInvariant();
        return name == "codex" || name.StartsWith("codex-", StringComparison.Ordinal);
    }

    /// <summary>codex's config.toml (CODEX_HOME, else ~/.codex), or null.</summary>
    public static string? ReadCodexConfig()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
                ? h
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var path = Path.Combine(home, "config.toml");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    private static readonly Regex Header = new(@"^\s*\[\s*([^\[\]]+?)\s*\]\s*(?:#.*)?$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyLine = new(@"^\s*([A-Za-z0-9_\-]+|""[^""]*""|'[^']*')\s*=\s*(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex PlainName = new(@"^[A-Za-z0-9_\-]{1,64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// The stdio MCP servers in codex's config that are a BrainX brain: the
    /// name starts with "brainx", the command runs a brainx binary, or its env
    /// names a BRAINX_ variable. Only plain names (letters, digits, '-', '_'),
    /// since codex splits an override key on dots.
    ///
    /// A deliberately small reader for the shapes `codex mcp add` and people
    /// write — <c>[mcp_servers.NAME]</c> with <c>command = …</c>, its
    /// <c>[mcp_servers.NAME.env]</c> table or an inline <c>env = { … }</c>,
    /// and <c>NAME = { command = …, env = { … } }</c> under <c>[mcp_servers]</c>
    /// — not a TOML parser. A shape it does not read yields no override, and
    /// the process-tree signal still covers that run.
    /// </summary>
    public static IReadOnlyList<string> BrainServersInCodexConfig(string? toml)
    {
        if (string.IsNullOrWhiteSpace(toml)) return Array.Empty<string>();
        var seen = new Dictionary<string, (bool Command, bool Url, bool Brain)>(StringComparer.Ordinal);
        void Note(string name, bool command = false, bool url = false, bool brain = false)
        {
            seen.TryGetValue(name, out var s);
            seen[name] = (s.Command || command, s.Url || url, s.Brain || brain);
        }

        string? server = null;      // inside [mcp_servers.NAME] or below it
        string? sub = null;         // the table under it: "env", "tools.x", …
        var inServersTable = false; // inside [mcp_servers] itself

        foreach (var raw in toml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var h = Header.Match(line);
            if (h.Success && !line.TrimStart().StartsWith("[[", StringComparison.Ordinal))
            {
                var path = SplitKey(h.Groups[1].Value);
                server = null; sub = null; inServersTable = false;
                if (path.Count == 1 && path[0] == CodexServersKey) inServersTable = true;
                else if (path.Count >= 2 && path[0] == CodexServersKey)
                {
                    server = path[1];
                    sub = path.Count > 2 ? string.Join('.', path.Skip(2)) : null;
                    Note(server, brain: server.StartsWith("brainx", StringComparison.OrdinalIgnoreCase));
                }
                continue;
            }
            if (line.TrimStart().StartsWith("[[", StringComparison.Ordinal)) { server = null; sub = null; inServersTable = false; continue; }

            var k = KeyLine.Match(line);
            if (!k.Success) continue;
            var key = Unquote(k.Groups[1].Value);
            var value = k.Groups[2].Value;

            if (server != null && sub == null)
            {
                if (key == "command") Note(server, command: true, brain: value.Contains("brainx", StringComparison.OrdinalIgnoreCase));
                else if (key == "url") Note(server, url: true);
                else if (key == "args" && value.Contains("brainx", StringComparison.OrdinalIgnoreCase)) Note(server, brain: true);
                else if (key == "env" && value.Contains("BRAINX_", StringComparison.Ordinal)) Note(server, brain: true);
            }
            else if (server != null && sub == "env")
            {
                if (key.StartsWith("BRAINX_", StringComparison.Ordinal)) Note(server, brain: true);
            }
            else if (inServersTable && value.TrimStart().StartsWith('{'))
            {
                // NAME = { command = "…", env = { BRAINX_VAULT = "…" } } on one line.
                var hasCommand = Regex.IsMatch(value, @"(^|[{,\s])command\s*=", RegexOptions.CultureInvariant);
                var hasUrl = Regex.IsMatch(value, @"(^|[{,\s])url\s*=", RegexOptions.CultureInvariant);
                Note(key, command: hasCommand, url: hasUrl,
                     brain: key.StartsWith("brainx", StringComparison.OrdinalIgnoreCase)
                            || value.Contains("brainx", StringComparison.OrdinalIgnoreCase));
            }
        }

        return seen.Where(p => p.Value.Command && !p.Value.Url && p.Value.Brain && PlainName.IsMatch(p.Key))
                   .Select(p => p.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// <summary>A dotted TOML key, with "quoted" and 'literal' segments.</summary>
    private static List<string> SplitKey(string key)
    {
        var parts = new List<string>();
        var cur = new StringBuilder();
        char quote = '\0';
        foreach (var c in key)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0'; else cur.Append(c);
                continue;
            }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c == '.') { parts.Add(cur.ToString().Trim()); cur.Clear(); continue; }
            cur.Append(c);
        }
        parts.Add(cur.ToString().Trim());
        return parts;
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1] : s;

    // ───────────── 2. the runs the broker started ─────────────

    /// <summary>
    /// Where the broker records its runners: beside the private browsing
    /// store (ChromeBridgeSettings.PrivateRoot) — local to this PC, never in
    /// the vault, never synced, and reachable from a codex-started brain
    /// because LOCALAPPDATA is on codex's allow-list. A sandbox without
    /// BRAINX_PRIVATE_DIR gets a temp folder, never the owner's.
    /// </summary>
    public static string DefaultDir => Path.Combine(ChromeBridgeSettings.PrivateRoot, "unattended-runs");

    private static string EntryPath(string dir, int pid, long created) =>
        Path.Combine(dir, pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-"
                          + created.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".run");

    /// <summary>
    /// Record a runner the broker just started. False when its creation time
    /// cannot be read (it already exited — nothing below it to protect) or the
    /// file cannot be written. Entries of runs that are gone are swept here.
    /// Never throws.
    /// </summary>
    public static bool Register(int pid, string agent, string? dir = null)
    {
        var created = CreationTime(pid);
        return created != null && Register(pid, created.Value, agent, dir);
    }

    /// <summary>Record (pid, creation time) — the harness's way to name a wrong time.</summary>
    public static bool Register(int pid, long created, string agent, string? dir = null)
    {
        try
        {
            // The default lives in the private folder, created owner-only like
            // the browsing store beside it.
            if (dir == null) PrivateBrowseStore.EnsurePrivateDirectory(ChromeBridgeSettings.PrivateRoot);
            dir ??= DefaultDir;
            Directory.CreateDirectory(dir);
            Sweep(dir);
            var path = EntryPath(dir, pid, created);
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, new JObject
            {
                ["agent"] = agent,
                ["pid"] = pid,
                ["registeredUtc"] = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            }.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
            AtomicFile.MoveOver(tmp, path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Forget a runner the broker reaped. Never throws.</summary>
    public static void Unregister(int pid, string? dir = null)
    {
        try
        {
            dir ??= DefaultDir;
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-*.run"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>The recorded runner entries in <paramref name="dir"/> (pid, creation time).</summary>
    public static IReadOnlyList<(int Pid, long Created)> Entries(string? dir = null)
    {
        var list = new List<(int, long)>();
        try
        {
            dir ??= DefaultDir;
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.EnumerateFiles(dir, "*.run"))
                if (ParseEntry(Path.GetFileNameWithoutExtension(f)) is { } e) list.Add(e);
        }
        catch { }
        return list;
    }

    private static (int Pid, long Created)? ParseEntry(string name)
    {
        var dash = name.IndexOf('-');
        if (dash <= 0) return null;
        return int.TryParse(name[..dash], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid)
               && long.TryParse(name[(dash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var created)
            ? (pid, created) : null;
    }

    /// <summary>Delete entries whose process is gone (or whose pid now belongs to another process).</summary>
    private static void Sweep(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "*.run").ToList())
        {
            if (ParseEntry(Path.GetFileNameWithoutExtension(f)) is not { } e) continue;
            if (CreationTime(e.Pid) == e.Created) continue;
            try { File.Delete(f); } catch { }
        }
        foreach (var t in Directory.EnumerateFiles(dir, "*.tmp").ToList())
            try { if (DateTime.UtcNow - File.GetLastWriteTimeUtc(t) > TimeSpan.FromMinutes(5)) File.Delete(t); } catch { }
    }

    /// <summary>Is (pid, created) a recorded runner?</summary>
    public static bool IsRegistered(int pid, long created, string? dir = null)
    {
        try { return File.Exists(EntryPath(dir ?? DefaultDir, pid, created)); }
        catch { return false; }
    }

    /// <summary>
    /// This process's ancestors, nearest first: (pid, creation time). The walk
    /// stops at a parent that cannot be opened (gone, or not ours to look at)
    /// and at one created AFTER its child — a parent pid the system has since
    /// handed to an unrelated process.
    /// </summary>
    public static IReadOnlyList<(int Pid, long Created)> Ancestors(int maxDepth = 16)
    {
        var chain = new List<(int, long)>();
        if (!OperatingSystem.IsWindows()) return chain;
        var pid = Environment.ProcessId;
        var created = CreationTime(pid);
        for (var depth = 0; depth < maxDepth; depth++)
        {
            var ppid = ParentPid(pid);
            if (ppid is not > 0 || ppid == pid) break;
            var pc = CreationTime(ppid.Value);
            if (pc == null) break;
            if (created != null && pc.Value > created.Value) break;
            chain.Add((ppid.Value, pc.Value));
            pid = ppid.Value;
            created = pc;
        }
        return chain;
    }

    /// <summary>Uncached: is any ancestor of this process a recorded runner in <paramref name="dir"/>?</summary>
    public static bool AncestorRegistered(string? dir = null)
    {
        dir ??= DefaultDir;
        foreach (var (pid, created) in Ancestors())
            if (IsRegistered(pid, created, dir)) return true;
        return false;
    }

    private static readonly object CacheGate = new();
    private static IReadOnlyList<(int Pid, long Created)>? _ancestors;
    private static bool _underRun;
    private static long _checkedAt = long.MinValue;
    private static readonly long RecheckMs = 2000;

    /// <summary>
    /// Is this process below a runner the broker recorded? A yes is kept for
    /// the life of the process (a run never becomes attended); a no is asked
    /// again after two seconds, because the broker records its runner a moment
    /// after starting it. The ancestor chain itself is read once.
    /// </summary>
    public static bool UnderRegisteredRun()
    {
        lock (CacheGate)
        {
            if (_underRun) return true;
            var now = Environment.TickCount64;
            if (_checkedAt != long.MinValue && now - _checkedAt < RecheckMs) return false;
            _checkedAt = now;
            try
            {
                _ancestors ??= Ancestors();
                var dir = DefaultDir;
                foreach (var (pid, created) in _ancestors)
                    if (IsRegistered(pid, created, dir)) { _underRun = true; break; }
            }
            catch { /* unknowable → the environment decides */ }
            return _underRun;
        }
    }

    /// <summary>A run the broker started: told by its environment, or found below a recorded runner.</summary>
    public static bool IsBrokerRun() =>
        Environment.GetEnvironmentVariable(BrokerRunEnv) == "1" || UnderRegisteredRun();

    // ───────────── Win32 ─────────────

    /// <summary>The process's creation time as a FILETIME (UTC, 100 ns ticks since 1601), or null.</summary>
    public static long? CreationTime(int pid)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            return GetProcessTimes(h, out var creation, out _, out _, out _) && creation > 0 ? creation : null;
        }
        finally { CloseHandle(h); }
    }

    private static int? ParentPid(int pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(h, 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return null;
            return (int)pbi.InheritedFromUniqueProcessId.ToInt64();
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }

    private const int ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref ProcessBasicInformation processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
