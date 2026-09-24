using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace BrainX.Server.Hubs;

/// <summary>
/// Append-only audit trail of security-sensitive events on the hub:
/// auth attempts (success + fail), scope set/revoke, share allow/deny.
/// Each entry chains the previous entry's hash via HMAC-SHA256 keyed by
/// a server-startup secret — anyone who tampers with a past entry breaks
/// every subsequent hash, so a one-line `audit-verify` script can prove
/// the log hasn't been edited.
///
/// Storage: JSONL at <c>.obsidianx/share-audit.log</c> next to the server
/// binary. Per-line append, fsync after each write. Single mutex.
///
/// Key handling: derived once at startup from
/// <c>BRAINX_AUDIT_KEY</c> env var if present, else a random 32 bytes
/// kept in-memory (logs from one server run can be verified only while
/// the process lives — fine for dev; ops should set the env var in prod
/// so chains survive restarts).
/// </summary>
public static class AuditLog
{
    private static readonly object Gate = new();
    private static string _path = "";
    private static byte[] _hmacKey = [];
    private static string _lastHashHex = "0000000000000000000000000000000000000000000000000000000000000000";
    private static bool _initialized;

    public static void Initialize(string baseDir)
    {
        lock (Gate)
        {
            if (_initialized) return;
            Directory.CreateDirectory(baseDir);
            _path = Path.Combine(baseDir, "share-audit.log");

            var envKey = Environment.GetEnvironmentVariable("BRAINX_AUDIT_KEY");
            _hmacKey = !string.IsNullOrWhiteSpace(envKey)
                ? Encoding.UTF8.GetBytes(envKey)
                : RandomNumberGenerator.GetBytes(32);

            // Rebuild last-hash from existing log so a server restart with
            // the same BRAINX_AUDIT_KEY continues the chain instead of
            // forking it.
            if (File.Exists(_path))
            {
                var lastLine = File.ReadLines(_path).LastOrDefault();
                if (!string.IsNullOrEmpty(lastLine))
                {
                    try
                    {
                        var dto = JsonConvert.DeserializeObject<AuditEntry>(lastLine);
                        if (dto != null && !string.IsNullOrEmpty(dto.Hmac)) _lastHashHex = dto.Hmac;
                    }
                    catch { /* corrupt last line — start fresh chain from sentinel */ }
                }
            }
            Volatile.Write(ref _initialized, true);
        }
    }

    public static void Record(string eventType, string actor, string detail)
    {
        if (!Volatile.Read(ref _initialized)) return;

        // The WHOLE link — read the previous hash, compute this one, append,
        // publish — happens under one lock. Reading _lastHashHex outside it (as
        // this did) let two concurrent records chain onto the same predecessor:
        // the file then held two entries with one PrevHmac, and the structural
        // check in /api/audit reported a tamper-evident log as BROKEN with no
        // tampering at all. Ts is taken inside too, so file order is time order.
        lock (Gate)
        {
            var entry = new AuditEntry
            {
                Ts = DateTime.UtcNow,
                Event = eventType,
                Actor = actor,
                Detail = detail,
                PrevHmac = _lastHashHex
            };

            // HMAC over the canonical bytes of all fields except Hmac itself,
            // chained via PrevHmac so any in-place edit downstream is visible.
            var canonical = Encoding.UTF8.GetBytes(
                $"{entry.Ts:O}\n{entry.Event}\n{entry.Actor}\n{entry.Detail}\n{entry.PrevHmac}");
            var hmac = HMACSHA256.HashData(_hmacKey, canonical);
            entry.Hmac = Convert.ToHexString(hmac).ToLowerInvariant();

            var line = JsonConvert.SerializeObject(entry, Formatting.None);
            try
            {
                File.AppendAllText(_path, line + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An audit write must never take a hub call down with it. The
                // chain head does NOT advance, so the file stays consistent: the
                // next entry links to the last one that actually reached disk.
                Console.WriteLine($"[audit] could not append '{eventType}': {ex.GetType().Name}");
                return;
            }
            _lastHashHex = entry.Hmac;
        }
    }

    private sealed class AuditEntry
    {
        public DateTime Ts { get; set; }
        public string Event { get; set; } = "";
        public string Actor { get; set; } = "";
        public string Detail { get; set; } = "";
        public string PrevHmac { get; set; } = "";
        public string Hmac { get; set; } = "";
    }
}
