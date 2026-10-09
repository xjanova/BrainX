using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// A fake <c>claude --claude-in-chrome-mcp</c>: the harness re-launched by
/// brainx-mcp's built-in chrome bridge. It replays the REAL tool list captured
/// from Claude Code 2.1.293 (fixtures/p293_tools.json) and answers each tool
/// with canned results that carry a per-run marker, so the checks can follow
/// every byte of "browsing data" and prove where it did — and did not — land.
///
/// It never touches a browser, the network or Anthropic. It is selected only
/// when BOTH the flag is the first argument (as the real binary requires) and
/// BRAINX_CHROME_STUB=1 is in the environment.
///
/// Its serverInfo.name is the marker the checks assert before trusting any
/// result: a bridge that somehow reached the real claude.exe would answer as
/// "Claude in Chrome".
/// </summary>
internal static class ChromeStubServer
{
    public const string EnvFlag = "BRAINX_CHROME_STUB";
    public const string MarkerEnv = "BRAINX_CHROME_STUB_MARKER";
    /// <summary>When set, the stub touches this file on start — proof it ran (or did not).</summary>
    public const string StartedEnv = "BRAINX_CHROME_STUB_STARTED";
    public const string ServerName = "brainx-chrome-stub";

    public static bool IsRequested(string[] args) =>
        Environment.GetEnvironmentVariable(EnvFlag) == "1"
        && args.Length > 0 && args[0] == "--claude-in-chrome-mcp";

    public static string Marker => Environment.GetEnvironmentVariable(MarkerEnv) ?? "NOMARKER";

    // What each tool hands back, keyed off the marker, so the checks can
    // recompute every value without talking to the stub.
    public static string MarkerUrl(string m) => $"https://private-{m.ToLowerInvariant()}.example.test/account";
    public static string PageText(string m) => $"PAGE-TEXT-{m} ยอดเงินคงเหลือ 12,345 บาท api_key=sk-ant-api03-{m}SECRETSECRETSECRETSECRET";
    public static string SecretValue(string m) => $"sk-ant-api03-{m}SECRETSECRETSECRETSECRET";
    public static byte[] ScreenshotBytes(string m)
    {
        // A PNG signature, then the marker, then filler — not a viewable
        // image, but bytes whose every copy can be searched for.
        var head = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var body = Encoding.ASCII.GetBytes($"SHOT-{m}-" + new string('x', 3000));
        return head.Concat(body).ToArray();
    }
    public static string ScreenshotBase64(string m) => Convert.ToBase64String(ScreenshotBytes(m));

    public static async Task<int> RunAsync()
    {
        try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }
        var started = Environment.GetEnvironmentVariable(StartedEnv);
        if (!string.IsNullOrWhiteSpace(started))
            try { File.AppendAllText(started, DateTime.UtcNow.ToString("o") + "\n"); } catch { }

        var tools = LoadFixtureTools();
        var stdout = Console.Out;
        string? line;
        while ((line = await Console.In.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JObject req;
            try { req = JObject.Parse(line); } catch { continue; }
            var id = req["id"];
            if (id == null || id.Type == JTokenType.Null) continue;   // notification

            JObject result = (req["method"]?.ToString()) switch
            {
                "initialize" => new JObject
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = true } },
                    ["serverInfo"] = new JObject { ["name"] = ServerName, ["version"] = "1.0.0" },
                },
                "tools/list" => new JObject { ["tools"] = tools },
                "tools/call" => Call(req["params"]?["name"]?.ToString() ?? "", req["params"]?["arguments"] as JObject ?? new JObject()),
                _ => new JObject(),
            };
            await stdout.WriteLineAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToString(Formatting.None));
            await stdout.FlushAsync();
        }
        return 0;   // stdin EOF — exactly how the real server leaves
    }

    private static JArray LoadFixtureTools()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "p293_tools.json");
        return JObject.Parse(File.ReadAllText(path))["result"]?["tools"] as JArray ?? new JArray();
    }

    private static JObject Text(string t) => new() { ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = t } } };

    private static JObject Call(string tool, JObject args)
    {
        var m = Marker;
        switch (tool)
        {
            case "tabs_context_mcp":
                return Text(new JObject
                {
                    ["tabs"] = new JArray { new JObject { ["tabId"] = 101, ["url"] = MarkerUrl(m), ["title"] = "Private page " + m } },
                }.ToString(Formatting.None));
            case "navigate":
                return Text($"Navigated to {args["url"]}");
            case "get_page_text":
                return Text(PageText(m));
            case "form_input":
                return Text($"Set value of {args["ref"]} to \"{args["value"]}\"");
            case "computer":
                if (args["action"]?.ToString() is "screenshot" or "zoom")
                    return new JObject
                    {
                        ["content"] = new JArray
                        {
                            new JObject { ["type"] = "text", ["text"] = "Successfully captured screenshot" },
                            new JObject { ["type"] = "image", ["data"] = ScreenshotBase64(m), ["mimeType"] = "image/png" },
                        },
                    };
                return Text($"Clicked at {args["coordinate"]}");
            case "list_connected_browsers":
                return Text(new JArray
                {
                    new JObject { ["deviceId"] = "dev-1", ["name"] = "Chrome (Default)", ["osPlatform"] = "win32", ["isLocal"] = true, ["inUse"] = true },
                }.ToString(Formatting.None));
            case "read_console_messages":
                // The environment probe: which parent-session variables reached us.
                var leaked = Environment.GetEnvironmentVariables().Keys.Cast<string>()
                    .Where(k => k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                                || k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
                                || k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(k => k).ToList();
                return Text("ENV-PROBE leaked=" + (leaked.Count == 0 ? "none" : string.Join(",", leaked))
                            + " args0=" + (Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() ?? ""));
            case "file_upload":
                return Text("FILE-UPLOAD-REACHED");
            default:
                return Text("ok " + tool);
        }
    }
}
