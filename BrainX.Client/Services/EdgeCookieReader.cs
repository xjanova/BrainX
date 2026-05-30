// ChromiumCookieReader — pulls decrypted cookies for a given host out
// of any Chromium-based browser's profile (Edge, Chrome, Brave) so we
// can re-inject them into a WebView2 instance. Used by ClaudeUsageProbe
// to scrape claude.ai/settings/usage without forcing the user to log
// in inside BrainX.
//
// All Chromium browsers store cookies the same way:
//   <profile>\Network\Cookies            — SQLite DB with encrypted_value BLOBs
//   <browser-root>\Local State           — JSON with os_crypt.encrypted_key
// "v10"/"v11" prefix → 12-byte AES-GCM nonce + ciphertext + 16-byte tag.
// AES key in Local State is base64; strip the 5-byte "DPAPI" header,
// then ProtectedData.Unprotect (CurrentUser scope) to recover it.
//
// The cookie DB is held with a SHARED lock by a running browser, but
// to keep this robust we copy the file to %TEMP% first (using
// FileShare.ReadWrite on the open). Decryption is purely local: no
// network calls, no UI.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BrainX.Client.Services;

public enum ChromiumBrowser { Edge, Chrome, Brave }

public sealed class ChromiumCookieReader
{
    public sealed record BrowserCookie(
        string Domain,
        string Name,
        string Value,
        string Path,
        bool Secure,
        bool HttpOnly,
        DateTime? ExpiresUtc,
        ChromiumBrowser Source);

    /// <summary>
    /// Read cookies from EVERY Chromium browser the user has installed
    /// (Edge + Chrome + Brave today), filtered to <paramref name="hostFilter"/>.
    /// Caller decides which list to inject — if both Edge and Chrome
    /// have a claude.ai session, Chrome's wins because we order
    /// chrome→edge→brave so the later Add overrides the earlier in
    /// WebView2's cookie manager.
    /// </summary>
    public static List<BrowserCookie> ReadAllForHost(string hostFilter)
    {
        var all = new List<BrowserCookie>();
        foreach (var browser in new[] { ChromiumBrowser.Chrome, ChromiumBrowser.Edge, ChromiumBrowser.Brave })
        {
            try { all.AddRange(ReadForHost(hostFilter, browser)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"{browser}: {ex.Message}"); }
        }
        return all;
    }

    // Single-file diagnostic log so we can see why a browser's cookies
    // come back empty. Wiped each app start by ClaudeUsageProbe.
    private static readonly string DiagLogPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrainX", "WebView2", "ClaudeUsageProbe", "probe.log");

    private static void Diag(string msg)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(DiagLogPath)!);
            System.IO.File.AppendAllText(DiagLogPath,
                $"[{DateTime.Now:HH:mm:ss}] ck: {msg}{Environment.NewLine}");
        }
        catch { }
    }

    public static List<BrowserCookie> ReadForHost(string hostFilter, ChromiumBrowser browser, string? profile = null)
    {
        var result = new List<BrowserCookie>();
        try
        {
            (string root, string defaultProfile) = ResolvePaths(browser);
            profile ??= defaultProfile;
            var cookiesPath = Path.Combine(root, profile, "Network", "Cookies");
            var localStatePath = Path.Combine(root, "Local State");
            if (!File.Exists(cookiesPath)) { Diag($"{browser}: cookies missing at {cookiesPath}"); return result; }
            if (!File.Exists(localStatePath)) { Diag($"{browser}: local state missing"); return result; }

            byte[]? key = LoadAesKey(localStatePath);
            if (key is null) { Diag($"{browser}: LoadAesKey returned null"); return result; }
            Diag($"{browser}: aes key OK ({key.Length} bytes)");

            var tmp = Path.Combine(Path.GetTempPath(), $"brainx_{browser}_cookies_{Guid.NewGuid():N}.db");
            try
            {
                try
                {
                    using var src = new FileStream(cookiesPath, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete);
                    using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write);
                    src.CopyTo(dst);
                    Diag($"{browser}: copied cookies db ({src.Length} bytes)");
                }
                catch (IOException ex)
                {
                    // Chrome 127+ may lock with FILE_SHARE_NONE — we can't
                    // read the live file. Fallback: try the SQLite -journal
                    // / -wal sidecar locations or just give up gracefully.
                    Diag($"{browser}: cookies file LOCKED by browser ({ex.Message}) — close the browser to read");
                    return result;
                }

                using var conn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Cache=Shared");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT host_key, name, encrypted_value, path,
                           is_secure, is_httponly, expires_utc
                    FROM cookies
                    WHERE host_key LIKE @h OR host_key LIKE @h2
                    """;
                var bare = hostFilter.TrimStart('.');
                cmd.Parameters.AddWithValue("@h", $"%{bare}");
                cmd.Parameters.AddWithValue("@h2", $"%.{bare}");

                using var reader = cmd.ExecuteReader();
                int seen = 0, decrypted = 0, decryptFailed = 0;
                var prefixCounts = new Dictionary<string, int>();
                while (reader.Read())
                {
                    seen++;
                    string host = reader.GetString(0);
                    string name = reader.GetString(1);
                    byte[] enc = (byte[])reader["encrypted_value"];
                    string path = reader.GetString(3);
                    bool secure = reader.GetInt64(4) != 0;
                    bool httpOnly = reader.GetInt64(5) != 0;
                    long expMicros = reader.GetInt64(6);

                    // Sniff prefix so we know if Chrome put it in v20 (app-bound).
                    string prefix = enc.Length >= 3
                        ? System.Text.Encoding.ASCII.GetString(enc, 0, 3)
                        : "?";
                    if (!prefixCounts.TryAdd(prefix, 1)) prefixCounts[prefix]++;

                    string? plain = TryDecrypt(enc, key);
                    if (plain is null) { decryptFailed++; continue; }
                    decrypted++;

                    DateTime? expires = null;
                    if (expMicros > 0)
                    {
                        try
                        {
                            var dt = new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                                .AddMicroseconds(expMicros);
                            if (dt.Year < 9000) expires = dt;
                        }
                        catch { }
                    }

                    result.Add(new BrowserCookie(host, name, plain, path, secure, httpOnly, expires, browser));
                }
                var prefixSummary = string.Join(", ",
                    prefixCounts.OrderByDescending(kv => kv.Value).Select(kv => $"'{kv.Key}'={kv.Value}"));
                Diag($"{browser}: rows={seen} decrypted={decrypted} failed={decryptFailed} prefixes=[{prefixSummary}]");
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
        catch (Exception ex)
        {
            Diag($"{browser}: outer exception {ex.GetType().Name}: {ex.Message}");
        }
        return result;
    }

    private static (string root, string defaultProfile) ResolvePaths(ChromiumBrowser browser)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return browser switch
        {
            ChromiumBrowser.Edge   => (Path.Combine(local, "Microsoft", "Edge", "User Data"),         "Default"),
            ChromiumBrowser.Chrome => (Path.Combine(local, "Google", "Chrome", "User Data"),          "Default"),
            ChromiumBrowser.Brave  => (Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"), "Default"),
            _ => throw new ArgumentOutOfRangeException(nameof(browser)),
        };
    }

    private static byte[]? LoadAesKey(string localStatePath)
    {
        try
        {
            var json = File.ReadAllText(localStatePath);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("os_crypt", out var osCrypt)) return null;
            if (!osCrypt.TryGetProperty("encrypted_key", out var ek)) return null;
            byte[] raw = Convert.FromBase64String(ek.GetString() ?? "");
            if (raw.Length < 5 || raw[0] != (byte)'D') return null;
            byte[] payload = new byte[raw.Length - 5];
            Array.Copy(raw, 5, payload, 0, payload.Length);
            byte[] key = ProtectedData.Unprotect(payload, null, DataProtectionScope.CurrentUser);
            return key;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ChromiumCookieReader.LoadAesKey: {ex.Message}");
            return null;
        }
    }

    private static string? TryDecrypt(byte[] enc, byte[] key)
    {
        if (enc.Length < 3) return null;
        try
        {
            if (enc.Length > 15 && (enc[0] == 'v') && (enc[1] == '1') && (enc[2] == '0' || enc[2] == '1'))
            {
                ReadOnlySpan<byte> nonce = new(enc, 3, 12);
                int cipherLen = enc.Length - 3 - 12 - 16;
                if (cipherLen <= 0) return null;
                ReadOnlySpan<byte> cipher = new(enc, 3 + 12, cipherLen);
                ReadOnlySpan<byte> tag = new(enc, enc.Length - 16, 16);
                byte[] plain = new byte[cipherLen];
                using var gcm = new AesGcm(key, 16);
                gcm.Decrypt(nonce, cipher, tag, plain);
                // Chrome 130+ wraps plain in a 32-byte prefix when secure_value
                // is set; strip if the trailing portion is printable ASCII/UTF-8.
                // We try the full string first and fall back to stripping the
                // header if the result starts with non-printable bytes.
                var s = System.Text.Encoding.UTF8.GetString(plain);
                if (s.Length > 0 && plain.Length > 32 && plain[0] < 0x20 && plain[1] < 0x20)
                {
                    // Header-stripped variant (Chrome v130+ "app-bound" encryption).
                    s = System.Text.Encoding.UTF8.GetString(plain, 32, plain.Length - 32);
                }
                return s;
            }
            byte[] dpapi = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(dpapi);
        }
        catch
        {
            return null;
        }
    }
}

// Back-compat shim so existing call sites (ClaudeUsageProbe) keep
// working. New code should call ChromiumCookieReader.ReadAllForHost
// directly.
public static class EdgeCookieReader
{
    public sealed record EdgeCookie(
        string Domain, string Name, string Value, string Path,
        bool Secure, bool HttpOnly, DateTime? ExpiresUtc);

    public static List<EdgeCookie> ReadForHost(string hostFilter)
    {
        var src = ChromiumCookieReader.ReadAllForHost(hostFilter);
        var result = new List<EdgeCookie>(src.Count);
        foreach (var c in src)
            result.Add(new EdgeCookie(c.Domain, c.Name, c.Value, c.Path, c.Secure, c.HttpOnly, c.ExpiresUtc));
        return result;
    }
}
