using BrainX.Core.Services;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// The built-in XDREAMER bridge (ai.xman4289.com): offline. What may leave this PC, and with what, is decided by
/// McpBridgeDef — so the checks are there: the file can never point a bridge off-box or attach a credential, the
/// built-in one may, over https only, and only for a key that looks like one.
/// </summary>
internal static partial class Program
{
    private static void RegisterXDreamerChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("xdreamer bridge: https remote only for the built-in def, never from mcp-bridges.json", XDreamerRemoteRules));
        checks.Add(("xdreamer bridge: key shape, mask, and the balance from get_credits", XDreamerKeyShape));
    }

    private static Task XDreamerRemoteRules()
    {
        var key = "axm-" + new string('A', 40);
        var def = XDreamerKey.BridgeDef(key)!;
        Check("a saved key makes an enabled built-in http bridge to the official endpoint",
              def is { Id: "xdreamer", Enabled: true, BuiltIn: true, AllowRemote: true, IsHttp: true } && def.Url == XDreamerKey.Endpoint
              && def.Validate() == null);
        Check("…its key travels in the Authorization header, nowhere else", def.Headers["Authorization"] == "Bearer " + key
              && !def.Fingerprint().Contains("axm-") && !(def.Setup ?? "").Contains("axm-A"));
        Check("…and survives CloneForSpawn", def.CloneForSpawn() is { AllowRemote: true } c && c.Headers["Authorization"] == "Bearer " + key);
        Check("no key, or not an axm- key → no bridge", XDreamerKey.BridgeDef(null) == null && XDreamerKey.BridgeDef("sk-" + new string('a', 40)) == null);

        var plain = def.CloneForSpawn();
        plain.Url = "http://ai.xman4289.com/api/mcp";
        Check("a remote bridge must be https", plain.Validate() is { } why && why.Contains("https"));
        var handWritten = new McpBridgeDef { Id = "xdreamer", Enabled = true, Url = XDreamerKey.Endpoint };
        Check("a hand-written entry pointing off this PC is still refused", handWritten.Validate() is { } w && w.Contains("loopback"));

        var vault = Path.Combine(Path.GetTempPath(), "brainx-xdreamer-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(vault, ".obsidianx"));
            File.WriteAllText(McpBridgeConfig.PathFor(vault),
                """{"version":1,"bridges":[{"id":"xd","enabled":true,"url":"https://evil.example/mcp","allowRemote":true,"headers":{"Authorization":"Bearer x"}},{"id":"local","enabled":true,"url":"http://127.0.0.1:9/mcp","headers":{"X-Key":"y"}}]}""");
            var defs = McpBridgeConfig.Load(vault, _ => { });
            Check("the file cannot opt into remote (allowRemote ignored → the entry is skipped)", defs.All(d => d.Id != "xd"));
            Check("…nor attach headers to a loopback bridge", defs.Single(d => d.Id == "local").Headers.Count == 0);
        }
        finally { try { Directory.Delete(vault, recursive: true); } catch { } }
        return Task.CompletedTask;
    }

    private static Task XDreamerKeyShape()
    {
        Check("axm- + letters/digits is a key; spaces, other prefixes and stubs are not",
              XDreamerKey.LooksLikeKey(" axm-" + new string('b', 40) + " ") && !XDreamerKey.LooksLikeKey("axm-short")
              && !XDreamerKey.LooksLikeKey("axm-" + new string('b', 20) + " x") && !XDreamerKey.LooksLikeKey("rk_live_" + new string('b', 40)));
        Check("the mask shows the ends only", XDreamerKey.Mask("axm-abcdefghijklmnop1234") == "axm-…1234");
        var structured = JObject.Parse("""{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"{\n  \"balance\": 1234\n}"}],"structuredContent":{"balance":1234},"isError":false}}""");
        var textOnly = JObject.Parse("""{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"{\"balance\": 77, \"top_up\": \"x\"}"}]}}""");
        Check("the balance is read from structuredContent, else from the text", XDreamerKey.BalanceFrom(structured) == 1234 && XDreamerKey.BalanceFrom(textOnly) == 77
              && XDreamerKey.BalanceFrom(null) == null);
        return Task.CompletedTask;
    }
}
