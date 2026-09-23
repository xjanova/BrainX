using System.Text.RegularExpressions;

namespace BrainX.Core.Services;

/// <summary>
/// Strips credentials out of text before it is written anywhere durable.
///
/// Measured 2026-09-23: the SSH audit trail in access-log.ndjson held 23 rows
/// with an inline credential (`mysql -p…`, `password=…`, bearer tokens) — the
/// full command string was logged verbatim, and SSH rows were exempt from
/// trimming, so every one of them was kept forever inside the vault. The
/// commands are the owner's and legitimately carry secrets; the log is the
/// part that must not.
///
/// Over-redaction is the cheap direction here. A masked port or a masked
/// `--auth-type` makes an audit line slightly less readable; a missed
/// password is a leak that outlives the session. When a pattern cannot be
/// evaluated inside its time budget the whole string is withheld rather than
/// written half-cleaned.
/// </summary>
public static class SecretRedactor
{
    public const string Mask = "***";

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);
    private const RegexOptions Ci = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    private static Regex R(string pattern, RegexOptions options = Ci) => new(pattern, options, Budget);

    private static readonly Regex PemKey =
        R(@"-----BEGIN ([A-Z0-9 ]*)PRIVATE KEY-----[\s\S]*?-----END \1PRIVATE KEY-----");

    // scheme://user:secret@host
    private static readonly Regex UrlCredentials =
        R(@"\b([a-z][a-z0-9+.\-]*://[^/\s:@'""]+):([^@\s/'""]+)@");

    private static readonly Regex AuthorizationHeader =
        R(@"\b(authorization\s*:\s*(?:bearer|basic|token|bot|digest)?\s*)[^\s'""]+");

    private static readonly Regex KeyHeaders =
        R(@"\b((?:x-api-key|api-key|x-auth-token|x-access-token|private-token)\s*:\s*)[^\s'""]+");

    // sshpass -p SECRET
    private static readonly Regex SshPass =
        R(@"(\bsshpass\s+-p\s*)(""[^""]*""|'[^']*'|\S+)");

    // curl -u user:SECRET / --user user:SECRET
    private static readonly Regex UserColonPassword =
        R(@"((?:^|\s)(?:-u|--user)\s+['""]?[^:\s'""]+:)([^\s'""]+)");

    // --password SECRET (space separated; the `=` form is SecretAssignment's)
    private static readonly Regex SpacedSecretFlag =
        R(@"(--(?:password|passwd|pass|token|secret|api-key|apikey|access-key|auth-token)\s+)(""[^""]*""|'[^']*'|[^\s\-]\S*)");

    // SQL: IDENTIFIED BY 'x' / PASSWORD('x')
    private static readonly Regex SqlIdentifiedBy =
        R(@"(\bidentified\s+(?:with\s+\S+\s+)?by\s+)('[^']*'|""[^""]*""|\S+)");
    private static readonly Regex SqlPasswordFunction =
        R(@"(\bpassword\s*\(\s*)('[^']*'|""[^""]*"")");

    // `wp config set DB_PASSWORD value`, `setx API_KEY value` — a secret NAME
    // followed by its value after a space instead of an `=`.
    private static readonly Regex SetSecretByName =
        R(@"(\b(?:config\s+set|config:set|setx|set)\s+[a-z0-9_.\-]*(?:pass(?:word|wd)?|pwd|secret|token|api[_\-]?key|apikey|private[_\-]?key)[a-z0-9_.\-]*\s+)(""[^""]*""|'[^']*'|[^\s&;'""]+)");

    // NAME=value / NAME: value where the name says it is a secret — env vars
    // (DB_PASSWORD=), flags (--dbpass=), query strings (?token=), JSON-ish.
    private static readonly Regex SecretAssignment =
        R(@"((?:^|[\s;&|""'?,({])-{0,2}[a-z0-9_.\-]*(?:pass(?:word|wd|phrase)?|pwd|secret|token|api[_\-]?key|apikey|access[_\-]?key|private[_\-]?key|client[_\-]?secret|auth(?:[_\-]?key)?|credentials?)[a-z0-9_.\-]*\s*[=:]\s*)(""[^""]*""|'[^']*'|[^\s&;'""]+)");

    // mysql -pSECRET — only meaningful next to a MySQL client, and case
    // SENSITIVE: -P is the port there, and `mkdir -p x` has a space after it.
    private static readonly Regex MysqlClient =
        R(@"\b(?:mysql|mysqldump|mysqladmin|mysqlcheck|mysqlimport|mysqlshow|mysqlpump|mariadb(?:-dump|-admin)?)\b");
    private static readonly Regex AttachedDashP =
        R(@"(?<=^|\s)-p(?![\s=]|$)(""[^""]*""|'[^']*'|\S+)", RegexOptions.CultureInvariant);

    private static readonly Regex KnownTokenShapes = R(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_\-]{20,}" +
        @"|sk-(?:proj-|ant-(?:api\d+-)?)?[A-Za-z0-9_\-]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}" +
        @"|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{35}|(?:sk|rk)_(?:live|test)_[0-9A-Za-z]{16,}" +
        @"|npm_[A-Za-z0-9]{36}|\d{8,10}:[A-Za-z0-9_\-]{35}" +
        @"|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,})\b",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The text with every recognised credential replaced by <see cref="Mask"/>.
    /// Never throws; a pattern that times out withholds the whole string.
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try
        {
            var s = text;
            s = PemKey.Replace(s, "-----BEGIN PRIVATE KEY----- " + Mask + " -----END PRIVATE KEY-----");
            s = UrlCredentials.Replace(s, "$1:" + Mask + "@");
            s = AuthorizationHeader.Replace(s, "$1" + Mask);
            s = KeyHeaders.Replace(s, "$1" + Mask);
            s = SshPass.Replace(s, "$1" + Mask);
            s = UserColonPassword.Replace(s, "$1" + Mask);
            s = SpacedSecretFlag.Replace(s, "$1" + Mask);
            s = SqlIdentifiedBy.Replace(s, "$1'" + Mask + "'");
            s = SqlPasswordFunction.Replace(s, "$1'" + Mask + "'");
            s = SetSecretByName.Replace(s, "$1" + Mask);
            s = SecretAssignment.Replace(s, "$1" + Mask);
            if (MysqlClient.IsMatch(s)) s = AttachedDashP.Replace(s, "-p" + Mask);
            s = KnownTokenShapes.Replace(s, Mask);
            return s;
        }
        catch (RegexMatchTimeoutException)
        {
            return "[withheld — too complex to redact safely]";
        }
    }

    /// <summary>True when <see cref="Redact"/> would change the text.</summary>
    public static bool ContainsSecret(string? text) =>
        !string.IsNullOrEmpty(text) && !string.Equals(Redact(text), text, StringComparison.Ordinal);
}
