using System.Net.Sockets;
using System.Text;
using BrainX.Core.Services;

namespace BrainX.Mcp.Bridge;

/// <summary>
/// Ask an engine, directly, whether it is alive — without spawning its MCP
/// server or dialling the bridge.
///
/// WHY this exists. The hub connects lazily: it only spawns the engine's server
/// on the first &lt;id&gt;__ call, so "no live bridge connection" is the resting
/// state of a perfectly healthy setup. The dashboard could therefore only ever
/// say "configured" — never "the editor is open and will answer" — and the two
/// look identical to whoever is watching, which is exactly the question they
/// are watching to answer.
///
/// WHY a plain TCP connect is not enough. A listening socket proves a process
/// bound a port, not that the editor's MAIN THREAD is alive to serve anything;
/// a crashed or frozen editor can leave the listener up. unity-mcp's own
/// PortDiscovery does a framed ping/pong for the same reason, and that is what
/// is reproduced here:
///
///   1. connect 127.0.0.1:port
///   2. Unity writes "WELCOME UNITY-MCP 1 FRAMING=1\n" — must contain FRAMING=1
///   3. send an 8-byte big-endian length followed by "ping"
///   4. read an 8-byte big-endian length + payload, which must contain
///      {"status":"success","result":{"message":"pong"}}
///
/// Everything is best-effort and hard-bounded: a probe that hangs must cost a
/// dim node on a dashboard, never a stalled agent.
/// </summary>
public static class EngineProbe
{
    /// <summary>Whole exchange, connect included. unity-mcp itself uses 300ms
    /// for the connect; the extra room is for the two framed reads.</summary>
    private const int BudgetMs = 700;

    /// <summary>Unity refuses to speak anything else, and a peer that does not
    /// announce framing is not the thing we are looking for.</summary>
    private const string Welcome = "FRAMING=1";

    /// <summary>A pong is tiny. Anything claiming otherwise is not a pong.</summary>
    private const int MaxReplyBytes = 64 * 1024;

    /// <summary>unity-mcp's PortManager.DefaultPort. It scans 6400..6499 when
    /// that one is taken, which is why the status file decides, not this.</summary>
    private const int UnityDefaultPort = 6400;

    /// <summary>How long a script recompile may keep the socket shut before the
    /// editor is called down. Its own client allows ~10s of reload retries.</summary>
    private const int ReloadGraceSeconds = 60;

    /// <summary>
    /// True only if the engine completed the handshake AND answered the ping.
    /// Never throws: any failure — closed port, wrong protocol, timeout, an
    /// editor mid-domain-reload — is simply "not reachable right now".
    /// </summary>
    public static bool IsAlive(McpBridgeProbe probe)
    {
        var port = ResolvePort(probe);
        if (port <= 0) return false;

        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(probe.Host, port);
            if (!connect.Wait(BudgetMs)) return false;
            if (!client.Connected) return false;

            client.ReceiveTimeout = BudgetMs;
            client.SendTimeout = BudgetMs;
            using var stream = client.GetStream();

            // A TCP-only probe stops here on purpose: some engines have no
            // application-level ping, and "the port answered" is still more
            // than the dashboard knew before.
            if (!probe.IsFramed) return true;

            if (!ReadWelcome(stream).Contains(Welcome, StringComparison.Ordinal)) return false;

            WriteFrame(stream, "ping"u8.ToArray());
            var reply = ReadFrame(stream);
            return reply != null &&
                   Encoding.UTF8.GetString(reply).Contains("\"message\":\"pong\"", StringComparison.Ordinal);
        }
        catch
        {
            // A domain reload — the editor recompiling scripts — drops every
            // TCP connection for a few seconds. That is an editor doing its job,
            // not an editor that is down, and reporting it as down would make
            // the node blink red every time the owner saves a C# file.
            return IsReloading(probe);
        }
    }

    /// <summary>
    /// Which port to talk to. A configured non-zero port wins. Otherwise ask
    /// unity-mcp's own status file, exactly as its Python client does: the
    /// editor moves to 6401+ when 6400 is taken, and a probe pinned to 6400
    /// would then report a perfectly healthy editor as down.
    /// </summary>
    private static int ResolvePort(McpBridgeProbe probe)
    {
        if (probe.Port > 0) return probe.Port;
        if (!probe.IsFramed) return 0;                      // nothing to discover
        return ReadStatus()?.Port ?? UnityDefaultPort;
    }

    private static bool IsReloading(McpBridgeProbe probe)
    {
        if (probe.Port > 0 && !probe.IsFramed) return false;
        var s = ReadStatus();
        return s is { Reloading: true } &&
               DateTime.UtcNow - s.WrittenUtc < TimeSpan.FromSeconds(ReloadGraceSeconds);
    }

    private sealed record UnityStatus(int Port, bool Reloading, DateTime WrittenUtc);

    /// <summary>
    /// The editor's heartbeat file, rewritten twice a second. Deleted on a
    /// graceful quit and left stale on a crash — which is precisely why it
    /// decides only WHICH PORT to ask, never whether anything is alive. That
    /// answer comes from the ping.
    /// </summary>
    private static UnityStatus? ReadStatus()
    {
        try
        {
            var dir = Environment.GetEnvironmentVariable("UNITY_MCP_STATUS_DIR")
                      ?? Path.Combine(
                          Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".unity-mcp");
            if (!Directory.Exists(dir)) return null;

            // One file per project. Newest wins — it is the editor most likely
            // to be the one the owner is looking at.
            var file = new DirectoryInfo(dir)
                .GetFiles("unity-mcp-status-*.json")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            if (file == null) return null;

            var o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(file.FullName));
            return new UnityStatus(
                o["unity_port"]?.ToObject<int>() ?? UnityDefaultPort,
                o["reloading"]?.ToObject<bool>() == true
                    || string.Equals(o["reason"]?.ToString(), "reloading", StringComparison.OrdinalIgnoreCase),
                file.LastWriteTimeUtc);
        }
        catch { return null; }
    }

    /// <summary>
    /// The greeting line, read one byte at a time up to the newline. Reading a
    /// block instead would swallow the head of the first frame, and the frames
    /// that follow are length-prefixed — losing their first bytes desynchronises
    /// the stream rather than producing a clean error.
    /// </summary>
    private static string ReadWelcome(NetworkStream stream)
    {
        var sb = new StringBuilder(64);
        for (int i = 0; i < 128; i++)
        {
            int b = stream.ReadByte();
            if (b < 0 || b == '\n') break;
            if (b != '\r') sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static void WriteFrame(NetworkStream stream, byte[] payload)
    {
        var header = new byte[8];
        // Big-endian uint64, which is what the other side reads. Writing this
        // in host order works on exactly the machines that never see a bug.
        for (int i = 0; i < 8; i++) header[7 - i] = (byte)(payload.LongLength >> (8 * i));
        stream.Write(header, 0, 8);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    private static byte[]? ReadFrame(NetworkStream stream)
    {
        var header = new byte[8];
        if (!ReadExactly(stream, header, 8)) return null;

        long length = 0;
        foreach (var b in header) length = (length << 8) | b;
        // A bogus or hostile length must not turn into a multi-gigabyte
        // allocation on the dashboard's timer thread.
        if (length <= 0 || length > MaxReplyBytes) return null;

        var payload = new byte[length];
        return ReadExactly(stream, payload, (int)length) ? payload : null;
    }

    private static bool ReadExactly(NetworkStream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }
}
