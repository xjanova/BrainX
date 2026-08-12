using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// A fake <c>brainx-mcp</c> child: speaks the same stdio JSON-RPC line protocol,
/// but does on command the things a real one only does by accident — answer late,
/// print junk to stdout, interleave a notification.
///
/// The harness spawns ITSELF in this mode (BRAINX_MCP_STUB=1 is inherited by the
/// child), so there is no second project to build and no live node, Ollama, vault
/// or token needed to prove the desync is gone.
///
/// Behaviour is keyed off the <c>query</c> argument rather than the tool name so
/// every call still goes through McpRemotePolicy's real allowlist — a stub tool
/// name would be default-denied at the HTTP layer and the test would prove
/// nothing about the path that matters.
/// </summary>
internal static class StubMcpServer
{
    public const string EnvFlag = "BRAINX_MCP_STUB";

    /// <summary>Sleep this many ms before answering: <c>__slow:3000__</c>.</summary>
    private const string SlowMarker = "__slow:";
    /// <summary>Emit stray stdout + a notification before the answer.</summary>
    private const string NoiseMarker = "__noise__";

    /// <summary>Text stamped into a slow call's result. If this ever comes back
    /// as the answer to a LATER call, the pipe has desynced — which is the whole
    /// thing under test.</summary>
    public const string SlowPayload = "SLOW-CALL-PAYLOAD-DO-NOT-DELIVER-LATE";
    public const string FastPayload = "FAST-CALL-PAYLOAD";

    public static async Task<int> RunAsync()
    {
        var stdout = Console.Out;
        string? line;
        while ((line = await Console.In.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JObject req;
            try { req = JObject.Parse(line.TrimStart('﻿')); }
            catch { continue; }

            var id = req["id"];
            var method = req["method"]?.ToString() ?? "";

            // Notifications get no reply — same contract as the real server.
            if (id == null || id.Type == JTokenType.Null) continue;

            string response;
            switch (method)
            {
                case "initialize":
                    response = Result(id, new JObject
                    {
                        ["protocolVersion"] = "2025-06-18",
                        ["capabilities"] = new JObject { ["tools"] = new JObject() },
                        ["serverInfo"] = new JObject { ["name"] = "brainx-stub", ["version"] = "0.0.0" },
                    });
                    break;

                case "tools/list":
                    response = Result(id, new JObject
                    {
                        ["tools"] = new JArray
                        {
                            new JObject { ["name"] = "brain_search", ["description"] = "stub" },
                            new JObject { ["name"] = "ssh_run",      ["description"] = "stub — must never be advertised remotely" },
                        },
                    });
                    break;

                case "tools/call":
                    response = await ToolsCallAsync(id, req["params"] as JObject, stdout);
                    break;

                default:
                    response = Result(id, new JObject());
                    break;
            }

            await stdout.WriteLineAsync(response);
            await stdout.FlushAsync();
        }
        return 0;
    }

    private static async Task<string> ToolsCallAsync(JToken id, JObject? parameters, TextWriter stdout)
    {
        var query = parameters?["arguments"]?["query"]?.ToString() ?? "";

        if (query.Contains(NoiseMarker, StringComparison.Ordinal))
        {
            // The three things a real server leaks onto the pipe that are NOT the
            // answer: a banner, a non-JSON warning, and a JSON-RPC notification.
            await stdout.WriteLineAsync("brainx-stub 0.0.0 — starting up");
            await stdout.WriteLineAsync("WARNING: this line is not JSON at all");
            await stdout.WriteLineAsync(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/message",
                ["params"] = new JObject { ["level"] = "info", ["data"] = "still working" },
            }.ToString(Formatting.None));
            await stdout.FlushAsync();
            return Content(id, FastPayload);
        }

        var slow = query.IndexOf(SlowMarker, StringComparison.Ordinal);
        if (slow >= 0)
        {
            var rest = query[(slow + SlowMarker.Length)..];
            var end = rest.IndexOf("__", StringComparison.Ordinal);
            if (end > 0 && int.TryParse(rest[..end], out var ms))
            {
                // Blocking the loop is the point: the real server handles one
                // message at a time, so the next request sits unread in the pipe
                // until this answer has been written.
                await Task.Delay(ms);
                return Content(id, SlowPayload);
            }
        }

        return Content(id, FastPayload);
    }

    private static string Content(JToken id, string text) => Result(id, new JObject
    {
        ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text } },
    });

    private static string Result(JToken id, JObject result) => new JObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,          // echoed verbatim, exactly like the real server
        ["result"] = result,
    }.ToString(Formatting.None);

    /// <summary>Build a tools/call body the stub understands.</summary>
    public static string CallBody(object id, string query) => new JObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = JToken.FromObject(id),
        ["method"] = "tools/call",
        ["params"] = new JObject
        {
            ["name"] = "brain_search",     // real allowlisted tool — goes through the real policy
            ["arguments"] = new JObject { ["query"] = query },
        },
    }.ToString(Formatting.None);

    public static string SlowQuery(int ms) => $"{SlowMarker}{ms}__";
    public static string NoisyQuery() => NoiseMarker;

    public static string InitBody(object id) => new JObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = JToken.FromObject(id),
        ["method"] = "initialize",
        ["params"] = new JObject
        {
            ["protocolVersion"] = "2025-06-18",
            ["capabilities"] = new JObject(),
            ["clientInfo"] = new JObject { ["name"] = "brainx-verify", ["version"] = "1.0" },
        },
    }.ToString(Formatting.None);

    static StubMcpServer()
    {
        // Payloads here are ASCII, but keep the encoding honest anyway: the
        // parent decodes this pipe as UTF-8 with no BOM.
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { /* redirected on some hosts */ }
    }
}
