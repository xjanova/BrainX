using System.Text;
using System.Text.RegularExpressions;

namespace BrainX.Core.Services;

/// <summary>
/// Which remote commands must never run without the owner seeing them first,
/// on EVERY profile — independent of the profile's own allow/deny regexes.
///
/// Why this lives in code and not in ssh-profiles.json: the profiles are
/// hand-written regex, and the 2026-09-23 review measured them leaking. On a
/// production profile `rm -rf /` passed (its pattern `\brm -rf /\b` needs a word
/// character after the slash) while `rm -rf /var/www/site/` was blocked, and
/// `dd of=/dev/sda`, `DROP DATABASE`, `curl … | sh` and `find / -delete` all
/// passed on both production profiles, which allow `^.+`. A classifier that is
/// tested once here protects every profile the owner will ever write.
///
/// Calibrated on 4,752 real commands the agents ran through ssh_run between
/// 2026-08-11 and 2026-09-23 so it gates what is rare and destructive and
/// leaves the daily work alone: `systemctl restart` (48×), `git reset --hard
/// &lt;sha&gt;` (16× — the deploy/rollback step), `artisan migrate` (10×),
/// `UPDATE … WHERE …` data fixes, SELECTs full of `&lt;` and `&lt;&gt;`, `rm -rf` of
/// mktemp dirs and /tmp scratch, and Laravel cache clears all stay ungated.
/// 20 of the 4,752 would have stopped for approval.
///
/// A hit does not refuse the command — it routes it through the owner's
/// approval (SshApprovalStore). The profile's own deny list stays the only
/// hard refusal.
/// </summary>
public static class SshCommandRisk
{
    public sealed record Hit(string Rule, string Reason, string ReasonTh, bool Catastrophic, string Excerpt);

    private const RegexOptions Ci = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);
    private static Regex R(string pattern) => new(pattern, Ci, Budget);

    /// <param name="OnSqlText">Match against the raw command instead of the
    /// shell view: SQL lives INSIDE quoted strings (`mysql -e "DROP …"`), and
    /// that is exactly the text the shell view neutralises.</param>
    private sealed record Rule(string Id, Regex Pattern, string Reason, string ReasonTh,
        bool Catastrophic = false, bool OnSqlText = false);

    /// <summary>
    /// Marks a word that came from a quoted DATA string (a SQL query, an echo
    /// label, a commit message). It is a letter to the regex engine (U+02B0,
    /// category Lm), so `\bdd` cannot match inside "ʰdd" and no command-position
    /// check mistakes "ʰrm" for rm — while the word itself survives, because a
    /// quoted path is still an argument.
    /// </summary>
    private const char DataMark = 'ʰ';

    // Commands that are dangerous by NAME — checked only in command position
    // (after sudo/env/nice, inside `bash -c`), never as a substring, because
    // `getent passwd` and `grep mkfs syslog` are reads.
    private static readonly HashSet<string> DiskCommands = new(StringComparer.Ordinal)
        { "mke2fs", "mkswap", "wipefs", "blkdiscard", "sgdisk", "gdisk" };
    private static readonly HashSet<string> PartitionCommands = new(StringComparer.Ordinal)
        { "fdisk", "sfdisk", "parted" };
    private static readonly HashSet<string> PowerCommands = new(StringComparer.Ordinal)
        { "shutdown", "reboot", "poweroff", "halt" };
    private static readonly HashSet<string> AccountCommands = new(StringComparer.Ordinal)
        { "passwd", "chpasswd", "useradd", "userdel", "usermod", "groupdel", "groupmod",
          "deluser", "delgroup", "adduser", "visudo", "vipw", "vigr" };

    private static readonly Rule[] PatternRules =
    {
        new("disk", R(@"\bdd\b[^;&|\n]*\bof=/dev/(?!null\b|zero\b|stdout\b|stderr\b|tty\b|shm/|fd/)\S+"),
            "dd writes straight onto a device", "dd เขียนทับอุปกรณ์โดยตรง", Catastrophic: true),
        new("disk", R(@"(?<![>&0-9])>(?!>)\s*/dev/(?:sd|hd|vd|xvd|nvme|mmcblk|md|dm-|disk|mapper/)\S*"),
            "redirects output onto a block device", "เขียนทับอุปกรณ์เก็บข้อมูล", Catastrophic: true),
        new("fork-bomb", R(@":\s*\(\s*\)\s*\{\s*:\s*\|\s*:\s*&\s*\}\s*;?\s*:"),
            "fork bomb", "fork bomb ทำให้เครื่องค้าง", Catastrophic: true),

        new("power", R(@"\bsystemctl\s+(?:poweroff|reboot|halt|kexec)\b"),
            "shuts down or reboots the server", "ปิดหรือรีบูตเซิร์ฟเวอร์"),
        new("service-stop", R(@"\bsystemctl\s+(?:--\S+\s+)*(?:stop|disable|mask|kill)\b|\bservice\s+\S+\s+stop\b|/etc/init\.d/\S+\s+stop\b|\bsupervisorctl\s+(?:stop|remove)\b"),
            "stops or disables a service", "หยุดหรือปิดบริการ"),
        new("kill-all", R(@"\bkill\s+(?:-\S+\s+)*-?1(?=\s|$|;)"),
            "kills every process", "ฆ่าทุกโปรเซส"),

        new("sql-drop-database", R(@"\bdrop\s+(?:database|schema)\b|\bdropdb\b|\bmysqladmin\b[^;&|\n]*\sdrop\b|\bdropDatabase\s*\("),
            "drops a whole database", "ลบฐานข้อมูลทั้งก้อน", Catastrophic: true, OnSqlText: true),
        new("sql-drop", R(@"\bdrop\s+(?:table|user|view|index|procedure|function|trigger|event|role)\b|\balter\s+table\s+\S+\s+drop\b|\btruncate\s+table\b"),
            "drops or truncates database objects", "ลบหรือล้างตารางในฐานข้อมูล", OnSqlText: true),
        // A redirect INTO a SQL client, on the shell view — so the `<` in
        // `mysql -e "SELECT … WHERE n < 5"` (a comparison, inside quotes) is not one.
        new("sql-import", R(@"\b(?:mysql|mariadb|psql)\b[^;&|\n]*(?<![<0-9])<(?!<)\s*[^\s<(]|\bpsql\b[^;&|\n]*\s-f\s|\bpg_restore\b[^;&|\n]*\s(?:--clean|-c)\b"),
            "loads a dump into a live database", "นำเข้า dump ทับฐานข้อมูลจริง"),
        new("sql-import", R(@"\bwp\s+db\s+(?:import|reset|drop|clean)\b|\bwp\s+site\s+empty\b|\bredis-cli\b[^;&|\n]*\b(?:flushall|flushdb)\b"),
            "wipes or reloads application data", "ล้างหรือโหลดข้อมูลแอปใหม่ทั้งหมด"),
        new("laravel-wipe", R(@"\bartisan\s+(?:migrate:(?:fresh|reset|refresh|rollback)|db:wipe)\b"),
            "rebuilds or wipes the Laravel schema", "ล้างหรือสร้าง schema ของ Laravel ใหม่", Catastrophic: true),

        new("pipe-to-shell", R(@"\b(?:curl|wget|fetch)\b[^;&\n]*\|\s*(?:sudo\s+(?:-\S+\s+)*)?(?:env\s+\S+=\S+\s+)*(?:ba|z|da|k|fi)?sh\b"),
            "runs a downloaded script", "รันสคริปต์ที่ดาวน์โหลดมาทันที"),
        new("pipe-to-shell", R(@"\b(?:curl|wget)\b[^;&\n]*\|\s*(?:sudo\s+)?(?:python\d?(?:\.\d+)?|perl|php|ruby|node)\s*-?\s*(?:$|[;&|)])"),
            "pipes a download into an interpreter", "ส่งไฟล์ที่ดาวน์โหลดเข้าตัวแปลภาษาโดยตรง"),
        new("pipe-to-shell", R(@"\b(?:(?:ba|z|da)?sh|source|\.)\s+<\(\s*(?:curl|wget)\b|\b(?:(?:ba|z)?sh\s+-c|eval)\s+\$\(\s*(?:curl|wget)\b"),
            "runs a downloaded script", "รันสคริปต์ที่ดาวน์โหลดมาทันที"),

        // `apt-get -s remove …` is a SIMULATION — measured twice in the history.
        new("pkg-remove", R(@"\b(?:apt|apt-get|aptitude)\b(?![^;&|\n]*\s(?:-s|--simulate|--dry-run|--just-print|--no-act)(?:\s|$))[^;&|\n]*\s(?:remove|purge|autoremove)\b|\b(?:yum|dnf|microdnf)\s+(?:-\S+\s+)*(?:remove|erase|autoremove)\b|\bzypper\s+(?:-\S+\s+)*(?:remove|rm)\b|\bpacman\s+-R|\bsnap\s+remove\b|\bdpkg\s+(?:-r|--remove|-P|--purge)\b|\brpm\s+(?:-e|--erase)\b"),
            "removes installed packages", "ถอนการติดตั้งแพ็กเกจ"),
        new("accounts", R(@"(?<![>&0-9])>(?!>)\s*/etc/(?:sudoers|passwd|shadow|group|gshadow)\b|\btee\s+(?:-\S+\s+)*/etc/(?:sudoers|passwd|shadow|group|gshadow)\b"),
            "rewrites an account or sudoers file", "เขียนทับไฟล์บัญชีผู้ใช้หรือ sudoers", Catastrophic: true),
        new("ssh-keys", R(@"(?<![>&0-9])>(?!>)\s*\S*authorized_keys\b|\b(?:rm|shred|truncate)\b[^;&|\n]*authorized_keys\b|\btee\s+(?!-a\b)\S*authorized_keys\b"),
            "replaces or deletes SSH authorized_keys", "เขียนทับหรือลบ authorized_keys"),
        new("firewall", R(@"\bip6?tables(?:-legacy|-nft)?\s+(?:-t\s+\w+\s+)?(?:-F|--flush|-X|--delete-chain|-P\s+\w+\s+(?:DROP|REJECT))\b|\bufw\s+(?:--force\s+)?(?:disable|reset)\b|\bnft\s+flush\s+ruleset\b|\bcsf\s+(?:-x|--disable|-f|--flush)\b|\bfirewall-cmd\b[^;&|\n]*--panic-on\b"),
            "flushes or disables the firewall", "ล้างหรือปิดไฟร์วอลล์"),
        new("crontab-remove", R(@"\bcrontab\s+(?:-u\s+\S+\s+)?-\w*r\b"),
            "deletes the whole crontab", "ลบ crontab ทั้งหมด"),
        new("git-clean", R(@"\bgit\s+(?:-C\s+\S+\s+)?clean\b[^;&|\n]*(?:\s-\w*f|--force)"),
            "git clean deletes untracked (and with -x ignored) files", "git clean ลบไฟล์ที่ไม่ได้อยู่ใน git"),
        new("git-force-push", R(@"\bgit\s+(?:-C\s+\S+\s+)?push\b[^;&|\n]*\s(?:-f\b|--force\b|--force-with-lease\b|--mirror\b|--delete\b|\+\S)"),
            "force-pushes or deletes remote history", "force push หรือลบประวัติบน remote"),
        new("docker-destroy", R(@"\bdocker\s+(?:system|volume|image|container|network|builder)\s+prune\b|\bdocker\s+volume\s+rm\b|\bdocker(?:-compose|\s+compose)\b[^;&|\n]*\sdown\b[^;&|\n]*(?:\s-v\b|--volumes|--rmi)|\bdocker\s+(?:rm|rmi)\b[^;&|\n]*(?:\s-\w*f\b|--force)"),
            "destroys containers, images or volumes", "ลบ container, image หรือ volume"),
        new("system-file-overwrite", R(@"(?<![>&0-9])>(?!>)\s*/(?:etc|boot|usr|bin|sbin|lib|lib64)/\S+|(?<![>&0-9])>(?!>)\s*/var/lib/\S+|(?<![>&0-9])>(?!>)\s*/root/\.ssh/\S+"),
            "overwrites a system file", "เขียนทับไฟล์ระบบ"),
        new("mv-to-null", R(@"\bmv\s+(?:-\S+\s+)*\S+\s+/dev/null\b"),
            "moves a file into /dev/null", "ย้ายไฟล์ทิ้งลง /dev/null"),
    };

    private static readonly Regex UnboundedWrite = R(@"\b(?:delete\s+from\s+[`""\w.$]+|update\s+[`""\w.{}$]+\s+set\b)");
    private static readonly Regex Where = R(@"\bwhere\b");
    private static readonly Regex SqlClient = R(@"\b(?:mysql|mariadb|psql|sqlite3?)\b|\bwp\s+db\s+query\b|\bDB::");
    private static readonly Regex BareTruncate = R(@"\btruncate\s+(?!-)[`""\w.]+\s*(?:;|$|[""'])");
    private static readonly Regex MktempVar = R(@"\b([A-Za-z_][A-Za-z0-9_]*)=[""']?(?:\$\(|`)\s*mktemp\b");
    private static readonly Regex BracedVar = new(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant, Budget);

    private static readonly Regex TempPath = new(@"^/(?:var/)?tmp/[^/*?\[\]]+", RegexOptions.CultureInvariant, Budget);
    private static readonly Regex VarRef = new(@"^\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?(?:/.*)?$", RegexOptions.CultureInvariant, Budget);
    private static readonly Regex SystemTopLevel = new(
        @"^/(?:bin|boot|dev|etc|home|lib|lib32|lib64|libx32|media|mnt|opt|proc|root|run|sbin|snap|srv|sys|usr|var)/?(?:\*|\.\*)?$",
        RegexOptions.CultureInvariant, Budget);
    // A whole home, a whole web root, the database files, the backups — and on
    // DirectAdmin servers, a whole site or its public_html.
    private static readonly Regex CriticalTree = new(
        @"^(?:/home/[^/]+|/var/(?:www|lib|lib/mysql|log|backups|spool)|/home/[^/]+/domains(?:/[^/]+(?:/public_html)?)?)/?(?:\*)?$",
        RegexOptions.CultureInvariant, Budget);
    private static readonly Regex CriticalFile = new(
        @"(?:^|/)\.env(?:\.(?!example|sample|dist)[\w.\-]+)?$|(?:^|/)wp-config\.php$|(?:^|/)authorized_keys$|(?:^|/)id_(?:rsa|dsa|ecdsa|ed25519)(?:\.pub)?$|^/etc/|^/boot/|^/var/lib/mysql|^/var/backups/",
        RegexOptions.CultureInvariant, Budget);
    // Regenerable framework caches — clearing them is maintenance, not loss.
    private static readonly Regex RegenerableCache = new(
        @"(?:^|/)(?:storage/framework/(?:cache|views)|bootstrap/cache)(?:/|$)", RegexOptions.CultureInvariant, Budget);
    private static readonly Regex LogFile = new(@"\.log(?:\.\d+)?$", RegexOptions.CultureInvariant, Budget);

    private static readonly HashSet<string> CatastrophicLiterals = new(StringComparer.Ordinal)
    {
        "/", "/*", "/.", "/.*", "*", ".", "./", "./*", "..", "../", "~", "~/", "~/*",
        "$HOME", "$HOME/", "$HOME/*", "${HOME}", "${HOME}/", "${HOME}/*"
    };

    /// <summary>
    /// Every reason this command needs the owner's approval — empty when it
    /// does not. Never throws; a command too convoluted to classify inside the
    /// time budget is treated as risky, not as safe.
    /// </summary>
    public static IReadOnlyList<Hit> Classify(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return Array.Empty<Hit>();
        var hits = new List<Hit>();
        try
        {
            var view = ShellView(command);
            foreach (var rule in PatternRules)
            {
                var source = rule.OnSqlText ? command : view;
                var m = rule.Pattern.Match(source);
                if (m.Success) hits.Add(new Hit(rule.Id, rule.Reason, rule.ReasonTh, rule.Catastrophic, Excerpt(source, m.Index)));
            }

            if (SqlClient.IsMatch(command))
            {
                var bare = BareTruncate.Match(command);
                if (bare.Success)
                    hits.Add(new Hit("sql-drop", "truncates a table", "ล้างข้อมูลทั้งตาราง", false, Excerpt(command, bare.Index)));
            }

            foreach (Match m in UnboundedWrite.Matches(command))
            {
                // The statement runs to the next `;` — no WHERE inside it means
                // every row. `UPDATE … WHERE id=…` data fixes are routine here.
                var end = command.IndexOf(';', m.Index);
                var stmt = end < 0 ? command[m.Index..] : command[m.Index..end];
                if (!Where.IsMatch(stmt))
                    hits.Add(new Hit("sql-unbounded", "UPDATE/DELETE without WHERE touches every row",
                        "UPDATE/DELETE ที่ไม่มี WHERE จะแก้ทุกแถว", false, Excerpt(command, m.Index)));
            }

            ClassifyByTargets(command, view, hits);
        }
        catch (RegexMatchTimeoutException)
        {
            hits.Add(new Hit("unclassifiable", "too complex to classify safely", "ซับซ้อนเกินกว่าจะตรวจได้อย่างปลอดภัย",
                false, Excerpt(command, 0)));
        }

        // One line per rule is enough for a human to judge; the first excerpt
        // is kept, and catastrophic wins if any instance was.
        return hits
            .GroupBy(h => h.Rule)
            .Select(g => g.First() with { Catastrophic = g.Any(h => h.Catastrophic) })
            .ToList();
    }

    /// <summary>
    /// Why this command would never finish on its own, or null. A remote
    /// `tail -f` or `watch` holds the brain's stdio loop until the profile's
    /// deadline — minutes of every other tool call in that session waiting on
    /// a log that will never end. `timeout N …` is the explicit way to ask for
    /// a bounded follow.
    /// </summary>
    public static string? NonTerminatingReason(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        try
        {
            foreach (var seg in Segments(ShellView(command)))
            {
                var head = CommandHead(seg);
                if (head.Count == 0) continue;
                var cmd = BaseName(head[0]);
                if (cmd == "timeout") continue;
                var flags = head.Skip(1).Where(t => t.Length > 1 && t[0] == '-').ToList();

                bool Short(char f) => flags.Any(t => t[1] != '-' && t.IndexOf(f, 1) > 0);
                bool Long(string name) => flags.Any(t => t == name || t.StartsWith(name + "=", StringComparison.Ordinal));

                switch (cmd)
                {
                    case "tail" when Short('f') || Short('F') || Long("--follow"):
                        return "tail -f never exits — use `tail -n N file`, or `timeout 20 tail -f file` for a bounded follow";
                    case "journalctl" when Short('f') || Long("--follow"):
                        return "journalctl -f never exits — use `journalctl -n N --no-pager`, or wrap it in `timeout N`";
                    case "watch":
                        return "watch never exits — run the command once instead";
                    case "ping" when !flags.Any(t => t.StartsWith("-c", StringComparison.Ordinal) || t.StartsWith("-w", StringComparison.Ordinal)):
                        return "ping without -c never exits — add `-c 4`";
                    case "tcpdump" when !flags.Any(t => t.StartsWith("-c", StringComparison.Ordinal)):
                        return "tcpdump without -c never exits — add `-c 100` or wrap it in `timeout N`";
                    case "vi" or "vim" or "nvim" or "nano" or "emacs" or "htop" or "mc":
                        return $"{cmd} is interactive and waits for a keyboard that is not there — use sed/cat/tee instead";
                    case "top" when !flags.Any(t => t.StartsWith("-b", StringComparison.Ordinal)):
                        return "top without -b waits for a terminal — use `top -b -n 1`";
                }
            }
        }
        catch (RegexMatchTimeoutException) { }
        return null;
    }

    /// <summary>
    /// ssh_tail's own path contract: POSIX path characters only, and never an
    /// option — `-f` used to pass and turned `tail -n 200 -f` into a follow.
    /// </summary>
    public static bool IsSafeTailPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && path[0] != '-'
        && Regex.IsMatch(path, @"^[/A-Za-z0-9._\-+:]+$", RegexOptions.CultureInvariant, Budget);

    // ───────────── the shell view ─────────────

    /// <summary>
    /// The command as the SHELL acts on it. Quoted strings are the hard part:
    /// `mysql -e "SELECT … WHERE n &lt; 5"` is data (that `&lt;` is a comparison,
    /// not a redirect — seven SELECTs in the history were misread before this),
    /// while `bash -c "rm -rf /x"` is code. So:
    ///   • a quoted string after `-c`, `eval`, `exec`, or as the remote command
    ///     of `ssh host "…"` is CODE and is inlined as-is;
    ///   • `$(…)` and backticks inside double quotes are code too — the shell
    ///     runs them — and become segments of their own;
    ///   • everything else quoted is DATA: its words stay (a quoted path is
    ///     still an argument) marked with <see cref="DataMark"/>, and its shell
    ///     metacharacters are neutralised.
    /// </summary>
    private static string ShellView(string command)
    {
        var sb = new StringBuilder(command.Length + 16);
        var heredocs = new Queue<(string Delimiter, bool StripTabs, bool IsCode)>();
        int i = 0;
        while (i < command.Length)
        {
            var c = command[i];
            if (c == '\\' && i + 1 < command.Length) { sb.Append(c).Append(command[i + 1]); i += 2; continue; }

            // `node - <<'EOF'` … `EOF`: the body is the program's stdin — data,
            // unless the program is a shell (`bash <<EOF`, `ssh host <<EOF`),
            // in which case every line of it is a command. Measured: three node
            // scripts in the history were misread as `mysql < dump` because
            // their JavaScript mentioned mariadb and used `<`.
            if (c == '<' && i + 1 < command.Length && command[i + 1] == '<'
                && (i + 2 >= command.Length || command[i + 2] != '<'))
            {
                var hd = HeredocOperator.Match(command, i);
                if (hd.Success)
                {
                    var delimiter = hd.Groups[2].Success ? hd.Groups[2].Value
                                  : hd.Groups[3].Success ? hd.Groups[3].Value
                                  : hd.Groups[4].Value;
                    heredocs.Enqueue((delimiter, hd.Groups[1].Value == "-", SegmentRunsShell(sb)));
                    sb.Append(' ');
                    i += hd.Length;
                    continue;
                }
            }
            if (c == '\n' && heredocs.Count > 0)
            {
                sb.Append('\n');
                i++;
                while (heredocs.Count > 0)
                {
                    var (delimiter, stripTabs, isCode) = heredocs.Dequeue();
                    var body = new StringBuilder();
                    while (i < command.Length)
                    {
                        var eol = command.IndexOf('\n', i);
                        var line = eol < 0 ? command[i..] : command[i..eol];
                        i = eol < 0 ? command.Length : eol + 1;
                        var probe = line.TrimEnd('\r');
                        if (stripTabs) probe = probe.TrimStart('\t');
                        if (probe == delimiter) break;
                        body.Append(line).Append('\n');
                    }
                    if (isCode) sb.Append(ShellView(body.ToString()));
                    else AppendData(sb, body.ToString());
                    sb.Append('\n');
                }
                continue;
            }
            if (c is '\'' or '"')
            {
                var content = new StringBuilder();
                int j = i + 1;
                while (j < command.Length && command[j] != c)
                {
                    if (c == '"' && command[j] == '\\' && j + 1 < command.Length) { content.Append(command[j + 1]); j += 2; continue; }
                    content.Append(command[j]);
                    j++;
                }
                var text = content.ToString();
                sb.Append(' ');
                if (QuotedIsCode(sb)) sb.Append(ShellView(text));
                else if (c == '"') AppendDoubleQuoted(sb, text);
                else AppendData(sb, text);
                sb.Append(' ');
                i = j < command.Length ? j + 1 : j;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static readonly Regex HeredocOperator = new(
        @"\G<<(-?)\s*(?:'([^'\n]+)'|""([^""\n]+)""|\\?([A-Za-z_][A-Za-z0-9_]*))", RegexOptions.CultureInvariant, Budget);

    /// <summary>Does the simple command being built read its stdin as shell commands?</summary>
    private static bool SegmentRunsShell(StringBuilder sb)
    {
        var s = sb.ToString();
        var sep = s.LastIndexOfAny(new[] { ';', '|', '&', '\n', '(', ')', '{', '}' });
        var words = (sep < 0 ? s : s[(sep + 1)..]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int k = 0;
        while (k < words.Length && (words[k] == "sudo" || words[k].StartsWith('-') || words[k].Contains('='))) k++;
        return k < words.Length && BaseName(words[k]) is "bash" or "sh" or "zsh" or "dash" or "ksh" or "ssh" or "sshpass";
    }

    private static bool QuotedIsCode(StringBuilder sb)
    {
        var s = sb.ToString().TrimEnd();
        var cut = s.LastIndexOfAny(new[] { ' ', '\t' });
        var last = cut < 0 ? s : s[(cut + 1)..];
        if (last is "-c" or "eval" or "exec" or "--command" or "-Command") return true;

        // `ssh host "cmd"` / `sudo ssh -p 22 host 'cmd'`: the string is the
        // remote command. Crude on purpose — a false "code" only means the
        // string is inspected like a command, which is the safe direction.
        var sep = s.LastIndexOfAny(new[] { ';', '|', '&', '\n', '(', ')', '{', '}' });
        var words = (sep < 0 ? s : s[(sep + 1)..]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int k = 0;
        while (k < words.Length && (words[k] == "sudo" || words[k].StartsWith('-'))) k++;
        return k < words.Length && BaseName(words[k]) is "ssh" or "sshpass";
    }

    private static void AppendDoubleQuoted(StringBuilder sb, string text)
    {
        var data = new StringBuilder();
        void Flush() { if (data.Length > 0) { AppendData(sb, data.ToString()); data.Clear(); } }

        int k = 0;
        while (k < text.Length)
        {
            if (text[k] == '$' && k + 1 < text.Length && text[k + 1] == '(')
            {
                int depth = 0, m = k + 1;
                for (; m < text.Length; m++)
                {
                    if (text[m] == '(') depth++;
                    else if (text[m] == ')' && --depth == 0) break;
                }
                Flush();
                sb.Append(" ; ").Append(ShellView(text[(k + 2)..Math.Min(m, text.Length)])).Append(" ; ");
                k = m + 1;
                continue;
            }
            if (text[k] == '`')
            {
                var m = text.IndexOf('`', k + 1);
                if (m < 0) m = text.Length;
                Flush();
                sb.Append(" ; ").Append(ShellView(text[(k + 1)..m])).Append(" ; ");
                k = m + 1;
                continue;
            }
            data.Append(text[k]);
            k++;
        }
        Flush();
    }

    private static void AppendData(StringBuilder sb, string text)
    {
        // ${T} → $T, so a quoted "${T}" is still recognisable as the mktemp dir.
        text = BracedVar.Replace(text, "$$$1");
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append(DataMark);
            foreach (var ch in word)
                sb.Append(ch is ';' or '|' or '&' or '<' or '>' or '(' or ')' or '{' or '}' or '`' ? '_' : ch);
            sb.Append(' ');
        }
    }

    // ───────────── target-based rules ─────────────

    private static void ClassifyByTargets(string command, string view, List<Hit> hits)
    {
        var tempVars = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in MktempVar.Matches(command)) tempVars.Add(m.Groups[1].Value);

        foreach (var seg in Segments(view))
        {
            ClassifyCommandWord(command, CommandHead(seg), hits);

            // Anywhere in the segment, not just first: `xargs rm -rf`,
            // `sudo -u www rm -r`, `bash -c "rm -rf …"` all delete. A word from
            // a quoted data string carries DataMark and never matches here.
            for (int i = 0; i < seg.Count; i++)
            {
                switch (BaseName(seg[i]))
                {
                    case "rm": ClassifyRm(command, seg, i, tempVars, hits); break;
                    case "find": ClassifyFind(command, seg, i, tempVars, hits); break;
                    case "shred": ClassifyShred(command, seg, i, tempVars, hits); break;
                    case "chmod" or "chown" or "chgrp": ClassifyPermissions(command, seg, i, hits); break;
                    case "truncate": ClassifyTruncate(command, seg, i, hits); break;
                }
            }
        }
    }

    private static void ClassifyCommandWord(string command, List<string> head, List<Hit> hits)
    {
        if (head.Count == 0) return;
        var cmd = BaseName(head[0]);
        var args = head.Skip(1).ToList();

        if (cmd.StartsWith("mkfs", StringComparison.Ordinal) || DiskCommands.Contains(cmd))
            hits.Add(new Hit("disk", "formats or wipes a disk", "ฟอร์แมตหรือล้างดิสก์", true, Excerpt(command, cmd)));
        else if (PartitionCommands.Contains(cmd) && !args.Any(a => a == "--list" || a.StartsWith("-l", StringComparison.Ordinal)))
            hits.Add(new Hit("disk", "repartitions a disk", "แบ่งพาร์ทิชันดิสก์ใหม่", true, Excerpt(command, cmd)));
        else if (PowerCommands.Contains(cmd) || (cmd is "init" or "telinit" && args.Count > 0 && args[0] is "0" or "6"))
            hits.Add(new Hit("power", "shuts down or reboots the server", "ปิดหรือรีบูตเซิร์ฟเวอร์", false, Excerpt(command, cmd)));
        else if (AccountCommands.Contains(cmd))
            hits.Add(new Hit("accounts", "changes user accounts or sudo rights", "แก้บัญชีผู้ใช้หรือสิทธิ์ sudo", false, Excerpt(command, cmd)));
        else if (cmd == "killall5")
            hits.Add(new Hit("kill-all", "kills every process", "ฆ่าทุกโปรเซส", false, Excerpt(command, cmd)));
    }

    /// <summary>
    /// The command a segment actually runs: past `sudo -u x`, `env A=b`,
    /// `nice -n 5`, and into `bash -c …` / `su -c …`.
    /// </summary>
    private static List<string> CommandHead(List<string> seg)
    {
        var head = StripPrefixes(seg);
        for (int depth = 0; depth < 3 && head.Count > 0; depth++)
        {
            if (BaseName(head[0]) is not ("bash" or "sh" or "zsh" or "dash" or "ksh" or "su")) break;
            var c = head.IndexOf("-c");
            if (c < 0 || c + 1 >= head.Count) break;
            head = StripPrefixes(head.Skip(c + 1).ToList());
        }
        return head;
    }

    private static void ClassifyRm(string command, List<string> seg, int at, HashSet<string> tempVars, List<Hit> hits)
    {
        bool recursive = false, noPreserveRoot = false, endOfOptions = false;
        var targets = new List<string>();
        for (int j = at + 1; j < seg.Count; j++)
        {
            var t = seg[j];
            if (IsRedirection(t, out var consumesNext)) { if (consumesNext) j++; continue; }
            if (!endOfOptions && t == "--") { endOfOptions = true; continue; }
            if (!endOfOptions && t.StartsWith("--", StringComparison.Ordinal))
            {
                if (t == "--recursive") recursive = true;
                if (t == "--no-preserve-root") noPreserveRoot = true;
                continue;
            }
            if (!endOfOptions && t.Length > 1 && t[0] == '-')
            {
                if (t.IndexOf('r') > 0 || t.IndexOf('R') > 0) recursive = true;
                continue;
            }
            targets.Add(t);
        }

        if (noPreserveRoot)
        {
            hits.Add(new Hit("rm-root", "rm with --no-preserve-root", "rm แบบปลดล็อกการลบทั้งเครื่อง", true, Excerpt(command, "rm")));
            return;
        }

        if (!recursive)
        {
            var critical = targets.FirstOrDefault(t => !IsTempTarget(t, tempVars) && CriticalFile.IsMatch(Unquote(t)));
            if (critical != null)
                hits.Add(new Hit("rm-critical-file", $"deletes {Unquote(critical)}", $"ลบไฟล์สำคัญ {Unquote(critical)}", false, Excerpt(command, "rm")));
            return;
        }

        if (targets.Count == 0)
        {
            // `xargs rm -rf`, or a list the shell expands later — nobody can
            // say what goes.
            hits.Add(new Hit("rm-recursive", "recursive delete of paths decided at run time",
                "ลบแบบ recursive โดยไม่รู้ล่วงหน้าว่าลบอะไร", false, Excerpt(command, "rm")));
            return;
        }

        var unsafeTargets = targets.Where(t => !IsTempTarget(t, tempVars)).ToList();
        if (unsafeTargets.Count == 0) return;

        var catastrophic = unsafeTargets.Any(IsCatastrophicTarget);
        var shown = string.Join(" ", unsafeTargets.Take(4).Select(Unquote));
        hits.Add(new Hit(catastrophic ? "rm-root" : "rm-recursive",
            $"recursive delete of {shown}", $"ลบทั้งโฟลเดอร์: {shown}", catastrophic, Excerpt(command, "rm")));
    }

    private static void ClassifyFind(string command, List<string> seg, int at, HashSet<string> tempVars, List<Hit> hits)
    {
        var roots = new List<string>();
        int j = at + 1;
        for (; j < seg.Count; j++)
        {
            var t = seg[j];
            if (t.StartsWith('-') || t.StartsWith('!') || t.StartsWith("\\(", StringComparison.Ordinal)) break;
            if (IsRedirection(t, out _)) break;
            roots.Add(t);
        }

        bool destructive = false;
        for (int k = j; k < seg.Count; k++)
        {
            var t = seg[k];
            if (t == "-delete") { destructive = true; break; }
            if (t is "-exec" or "-execdir" or "-ok" or "-okdir" && k + 1 < seg.Count
                && BaseName(seg[k + 1]) is "rm" or "shred" or "unlink" or "truncate")
            { destructive = true; break; }
        }
        if (!destructive) return;

        if (roots.Count > 0 && roots.All(r => IsTempTarget(r, tempVars) || RegenerableCache.IsMatch(Unquote(r).TrimEnd('/') + "/")))
            return;

        var catastrophic = roots.Count > 0 && roots.Any(IsCatastrophicTarget);
        var where = roots.Count == 0 ? "the current directory" : string.Join(" ", roots.Take(3).Select(Unquote));
        hits.Add(new Hit("find-delete", $"find deletes everything it matches under {where}",
            $"find ลบทุกไฟล์ที่ตรงเงื่อนไขใต้ {where}", catastrophic, Excerpt(command, "find")));
    }

    private static void ClassifyShred(string command, List<string> seg, int at, HashSet<string> tempVars, List<Hit> hits)
    {
        var targets = new List<string>();
        for (int j = at + 1; j < seg.Count; j++)
        {
            var t = seg[j];
            if (IsRedirection(t, out var consumesNext)) { if (consumesNext) j++; continue; }
            if (t is "-n" or "-s" or "--iterations" or "--size" or "--random-source") { j++; continue; }
            if (t.StartsWith('-')) continue;
            targets.Add(t);
        }
        if (targets.Any(t => Unquote(t).StartsWith("/dev/", StringComparison.Ordinal)))
        {
            hits.Add(new Hit("disk", "shred overwrites a device", "shred เขียนทับอุปกรณ์เก็บข้อมูล", true, Excerpt(command, "shred")));
            return;
        }
        var kept = targets.Where(t => !IsTempTarget(t, tempVars)).ToList();
        if (kept.Count == 0) return;
        var shown = string.Join(" ", kept.Take(3).Select(Unquote));
        hits.Add(new Hit("shred", $"irrecoverably destroys {shown}", $"ทำลายไฟล์แบบกู้คืนไม่ได้: {shown}", false, Excerpt(command, "shred")));
    }

    private static void ClassifyPermissions(string command, List<string> seg, int at, List<Hit> hits)
    {
        var name = BaseName(seg[at]);
        bool recursive = false, sawSpec = false;
        var targets = new List<string>();
        for (int j = at + 1; j < seg.Count; j++)
        {
            var t = seg[j];
            if (IsRedirection(t, out var consumesNext)) { if (consumesNext) j++; continue; }
            if (t == "--recursive") { recursive = true; continue; }
            if (t.StartsWith("--reference", StringComparison.Ordinal)) { sawSpec = true; continue; }
            if (t.StartsWith("--", StringComparison.Ordinal)) continue;
            // chmod modes can themselves start with '-' (`chmod -x file`), so
            // only a known flag cluster counts as options.
            if (t.Length > 1 && t[0] == '-' && t.Skip(1).All(c => c is 'R' or 'f' or 'v' or 'c' or 'h' or 'H' or 'L' or 'P'))
            {
                if (t.Contains('R')) recursive = true;
                continue;
            }
            if (!sawSpec) { sawSpec = true; continue; }   // the mode / owner:group
            targets.Add(t);
        }

        var hit = targets.FirstOrDefault(t => CatastrophicLiterals.Contains(Unquote(t)) || SystemTopLevel.IsMatch(Unquote(t))
                                              || (recursive && CriticalTree.IsMatch(Unquote(t))));
        if (hit == null) return;
        hits.Add(new Hit("perm-system", $"{name}{(recursive ? " -R" : "")} on {Unquote(hit)}",
            $"{name}{(recursive ? " -R" : "")} บน {Unquote(hit)} กระทบทั้งระบบ", recursive, Excerpt(command, name)));
    }

    private static void ClassifyTruncate(string command, List<string> seg, int at, List<Hit> hits)
    {
        bool zero = false;
        var targets = new List<string>();
        for (int j = at + 1; j < seg.Count; j++)
        {
            var t = seg[j];
            if (IsRedirection(t, out var consumesNext)) { if (consumesNext) j++; continue; }
            if (t is "-s" or "--size") { if (j + 1 < seg.Count && seg[j + 1] == "0") zero = true; j++; continue; }
            if (t is "-s0" or "--size=0") { zero = true; continue; }
            if (t.StartsWith('-')) continue;
            targets.Add(t);
        }
        if (!zero) return;
        var hit = targets.FirstOrDefault(t => !LogFile.IsMatch(Unquote(t)));
        if (hit == null) return;
        hits.Add(new Hit("truncate-file", $"empties {Unquote(hit)}", $"ล้างเนื้อหาไฟล์ {Unquote(hit)} จนว่าง", false, Excerpt(command, "truncate")));
    }

    // ───────────── parsing helpers ─────────────

    private static bool IsTempTarget(string raw, HashSet<string> tempVars)
    {
        var t = Unquote(raw);
        var v = VarRef.Match(t);
        if (v.Success) return tempVars.Contains(v.Groups[1].Value);
        if (t.Contains("/../", StringComparison.Ordinal) || t.EndsWith("/..", StringComparison.Ordinal)) return false;
        return TempPath.IsMatch(t);
    }

    private static bool IsCatastrophicTarget(string raw)
    {
        var t = Unquote(raw);
        return CatastrophicLiterals.Contains(t) || SystemTopLevel.IsMatch(t) || CriticalTree.IsMatch(t);
    }

    /// <summary>The argument itself: no quotes, no data mark.</summary>
    private static string Unquote(string t) => t.Replace(DataMark.ToString(), "").Trim().Trim('"', '\'');

    /// <summary>
    /// The command a word names. Deliberately keeps <see cref="DataMark"/>: a
    /// word that came from a quoted data string is never a command.
    /// </summary>
    private static string BaseName(string token)
    {
        var t = token.Trim();
        var slash = t.LastIndexOf('/');
        return slash >= 0 && t[0] != DataMark ? t[(slash + 1)..] : t;
    }

    /// <summary>`2&gt;/dev/null`, `&gt;&gt; x`, `&lt;in` — none of them are targets.</summary>
    private static bool IsRedirection(string t, out bool consumesNext)
    {
        consumesNext = false;
        var m = Regex.Match(t, @"^(?:\d*|&)(?:>>?|<)(&\d+)?(.*)$", RegexOptions.CultureInvariant, Budget);
        if (!m.Success) return false;
        consumesNext = m.Groups[1].Value.Length == 0 && m.Groups[2].Value.Length == 0;
        return true;
    }

    /// <summary>Leading `sudo -u x`, `nice -n 5`, `env A=b` — the command is what follows.</summary>
    private static List<string> StripPrefixes(List<string> seg)
    {
        int i = 0;
        while (i < seg.Count)
        {
            var b = BaseName(seg[i]);
            if (b is "sudo" or "nice" or "nohup" or "env" or "stdbuf" or "ionice" or "time" or "command" or "exec")
            {
                i++;
                while (i < seg.Count && (seg[i].StartsWith('-') || seg[i].Contains('=')))
                {
                    // flags that take a value: sudo -u user, nice -n 5
                    if (seg[i] is "-u" or "-g" or "-n" or "-c" or "-o" or "-e") i++;
                    i++;
                }
                continue;
            }
            if (seg[i].Contains('=') && !seg[i].StartsWith('-') && seg[i][0] != DataMark) { i++; continue; }   // VAR=x cmd
            break;
        }
        return seg.Skip(i).ToList();
    }

    /// <summary>Split a shell view into simple commands.</summary>
    private static IEnumerable<List<string>> Segments(string view)
    {
        var cur = new StringBuilder();
        var segs = new List<string>();
        var inVarBrace = false;   // ${NAME} is a word, not a block
        for (int i = 0; i < view.Length; i++)
        {
            var c = view[i];
            if (c == '{' && i > 0 && view[i - 1] == '$') { inVarBrace = true; cur.Append(c); continue; }
            if (c == '}' && inVarBrace) { inVarBrace = false; cur.Append(c); continue; }
            switch (c)
            {
                case ';' or '\n' or '\r' or '|' or '(' or ')' or '{' or '}' or '`':
                    segs.Add(cur.ToString()); cur.Clear();
                    break;
                case '&':
                    var prev = i > 0 ? view[i - 1] : ' ';
                    var next = i + 1 < view.Length ? view[i + 1] : ' ';
                    if (prev == '>' || next == '>') cur.Append(c);          // 2>&1, &>file
                    else { segs.Add(cur.ToString()); cur.Clear(); }
                    break;
                default:
                    cur.Append(c);
                    break;
            }
        }
        segs.Add(cur.ToString());
        foreach (var s in segs)
        {
            var tokens = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (tokens.Count > 0) yield return tokens;
        }
    }

    private static string Excerpt(string source, string word)
    {
        var i = source.IndexOf(word, StringComparison.Ordinal);
        return Excerpt(source, i < 0 ? 0 : i);
    }

    private static string Excerpt(string source, int index)
    {
        var start = Math.Clamp(index, 0, Math.Max(0, source.Length - 1));
        var len = Math.Min(80, source.Length - start);
        var raw = source.Substring(start, len).Replace(DataMark.ToString(), "").Replace('\n', ' ').Replace('\r', ' ');
        return SecretRedactor.Redact(raw).Trim() + (start + len < source.Length ? "…" : "");
    }
}
