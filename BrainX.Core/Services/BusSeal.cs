using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Core.Services;

/// <summary>
/// Proof that a cowork line really came from the owner's BrainX window.
///
/// The room is a folder of JSON files, and until 2026-09-23 the `from` field
/// was believed as written. A line saying `"from":"owner"` was announced to
/// every agent as "THE OWNER SPOKE … it outranks peer chatter", and the broker
/// dispatched it — spawning a headless `codex exec --approve-for-me` to carry
/// out whatever it said. Any agent or process that could write one file into
/// agent-bus/cowork/messages could speak as the owner.
///
/// Now the BrainX client seals what it writes as the owner: an HMAC over the
/// line with a key only this Windows account can unwrap (DPAPI), and the MCP
/// honours `from: owner` only when the seal checks. Same limits as the SSH
/// approvals: it stops a line written by hand, an injected instruction, a
/// confused peer — not code running as the owner that goes looking for the key.
///
/// Rollout: until a sealing client has run on this machine there is no key,
/// and unsealed owner lines are accepted as before (a room that stopped
/// hearing its owner the moment the MCP updated would be the worse bug). The
/// client creates the key at startup, so the gate closes the first time the
/// updated app opens.
/// </summary>
public static class BusSeal
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BrainX.bus-seal.v1");
    private static readonly object Gate = new();
    private static byte[]? _key;
    private static string? _keyPathLoaded;

    public static string DefaultKeyPath =>
        Environment.GetEnvironmentVariable("BRAINX_BUS_SEAL_KEY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrainX", "bus-seal.key");

    /// <summary>True once a sealing client has created the key — from then on an unsealed owner line is a forgery.</summary>
    public static bool IsActive(string? keyPath = null) => File.Exists(keyPath ?? DefaultKeyPath);

    /// <summary>Create the key if it does not exist yet. The client calls this at startup.</summary>
    public static void EnsureKey(string? keyPath = null) => Key(keyPath ?? DefaultKeyPath, create: true);

    /// <summary>Add a `seal` to a message about to be written.</summary>
    public static void Seal(JObject message, string? keyPath = null)
    {
        var key = Key(keyPath ?? DefaultKeyPath, create: true);
        message["seal"] = Convert.ToBase64String(HMACSHA256.HashData(key, Canonical(message)));
    }

    /// <summary>Does this message carry a seal made with this machine's key?</summary>
    public static bool Verify(JObject message, string? keyPath = null)
    {
        var path = keyPath ?? DefaultKeyPath;
        if (!File.Exists(path)) return false;
        if (message["seal"]?.Type != JTokenType.String) return false;
        byte[] given;
        try { given = Convert.FromBase64String(message["seal"]!.ToString()); }
        catch (FormatException) { return false; }
        byte[] key;
        try { key = Key(path, create: false); }
        catch { return false; }
        return CryptographicOperations.FixedTimeEquals(given, HMACSHA256.HashData(key, Canonical(message)));
    }

    /// <summary>
    /// The fields that make a line what it is. Not `ts`: JSON readers re-render
    /// dates by culture, and the id already carries the tick it was written at.
    /// </summary>
    private static byte[] Canonical(JObject m) => Encoding.UTF8.GetBytes(string.Join("\u001f",
        "v1",
        m["id"]?.ToString() ?? "",
        m["from"]?.ToString() ?? "",
        m["to"]?.ToString() ?? "",
        m["topic"]?.ToString() ?? "",
        m["body"]?.ToString() ?? ""));

    private static byte[] Key(string path, bool create)
    {
        lock (Gate)
        {
            if (_key != null && _keyPathLoaded == path) return _key;
            if (File.Exists(path))
            {
                var stored = File.ReadAllBytes(path);
                _key = OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser)
                    : stored;
                _keyPathLoaded = path;
                return _key;
            }
            if (!create) throw new FileNotFoundException("no bus seal key", path);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var key = RandomNumberGenerator.GetBytes(32);
            var bytes = OperatingSystem.IsWindows()
                ? ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser)
                : key;
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(tmp, bytes);
            try { File.Move(tmp, path, overwrite: false); }
            catch (IOException)
            {
                // Another process made it first — theirs is the key.
                try { File.Delete(tmp); } catch { }
                _key = null;
                return Key(path, create: false);
            }
            _key = key;
            _keyPathLoaded = path;
            return _key;
        }
    }
}
