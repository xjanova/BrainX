using System.Text;
using Newtonsoft.Json.Linq;

namespace BrainX.Tests;

/// <summary>
/// agent_avatar (2026-10-06). Owner: "เราอนุญาติให้ เอไอสร้างอวาต้าเอง จะเปลี่ยน
/// จะสุ่ม ได้ แต่ให้มีรูปร่าง ที่สมควร" — an agent may change or roll its look,
/// but the figure stays a decent one: skin from the human palette, an outfit
/// that is not skin-coloured, and a desk name that is not somebody else's.
/// </summary>
internal static partial class Program
{
    private static void RegisterAvatarChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("agent_avatar: free to change or roll, always a decent figure", AvatarStaysDecent));
    }

    private static readonly string[] TestSkins = { "#f6d9bd", "#eec39a", "#d9a06b", "#b97a4e", "#8d5524", "#5c3a1e" };

    private static async Task AvatarStaysDecent()
    {
        var exe = FindMcpExe();
        if (exe == null) { Check("brainx-mcp.exe (built) exists for the check", false); return; }

        var root = Path.Combine(Path.GetTempPath(), "brainx-avatar-" + Guid.NewGuid().ToString("N"));
        var vault = Path.Combine(root, "vault");
        var presence = Path.Combine(vault, ".obsidianx", "agent-bus", "presence");
        Directory.CreateDirectory(presence);
        Directory.CreateDirectory(Path.Combine(vault, "Notes"));
        // Another agent at its desk, so its name is taken.
        File.WriteAllText(Path.Combine(presence, "claude.json"),
            new JObject { ["lastSeenUtc"] = DateTime.UtcNow.ToString("o") }.ToString(), new UTF8Encoding(false));

        try
        {
            await using var me = await StartBusSession(exe, vault, "avatar-test-key", "codex");

            var read = await me.Call("agent_avatar", new JObject());
            Check("no arguments is a read, and offers random", read["avatar"] is JObject && (read["hint"]?.ToString() ?? "").Contains("random"), read.ToString());

            for (var i = 0; i < 12; i++)
            {
                var rolled = await me.Call("agent_avatar", new JObject { ["random"] = true });
                var av = rolled["avatar"] as JObject;
                var skin = av?["skin"]?.ToString() ?? "";
                var outfit = av?["outfit"]?.ToString() ?? "";
                if (rolled["saved"]?.Value<bool>() != true || !TestSkins.Contains(skin) || (outfit.Length > 0 && FarFromSkin(outfit) == false))
                {
                    Check("random rolls a skin from the palette and a clothing-coloured outfit", false, rolled.ToString());
                    return;
                }
            }
            Check("random rolls a skin from the palette and a clothing-coloured outfit, every time", true);

            var kept = await me.Call("agent_avatar", new JObject { ["random"] = true, ["hair"] = "bun", ["gender"] = "f" });
            Check("…and keeps the fields named alongside it",
                  kept["avatar"]?["hair"]?.ToString() == "bun" && kept["avatar"]?["gender"]?.ToString() == "f", kept.ToString());

            var picked = await me.Call("agent_avatar", new JObject { ["skin"] = 2 });
            Check("skin by index", picked["avatar"]?["skin"]?.ToString() == "#d9a06b", picked.ToString());

            var green = await me.Call("agent_avatar", new JObject { ["skin"] = "#00ff00", ["accessory"] = "glasses" });
            Check("a free skin colour is refused and named under ignored",
                  green["avatar"]?["skin"]?.ToString() == "#d9a06b" && green["ignored"]?["skin"] != null, green.ToString());
            Check("…the rest of the same call still applies", green["avatar"]?["accessory"]?.ToString() == "glasses", green.ToString());

            var bare = await me.Call("agent_avatar", new JObject { ["outfit"] = "#e0a872" });
            Check("a skin-coloured outfit is refused", bare["ignored"]?["outfit"] != null
                  && bare["avatar"]?["outfit"]?.ToString() != "#e0a872", bare.ToString());

            var dressed = await me.Call("agent_avatar", new JObject { ["outfit"] = "#2f5d8a" });
            Check("a clothing colour is worn", dressed["avatar"]?["outfit"]?.ToString() == "#2f5d8a" && dressed["ignored"] == null, dressed.ToString());

            var taken = await me.Call("agent_avatar", new JObject { ["display"] = "Claude" });
            Check("another agent's name is not a desk name", taken["ignored"]?["display"] != null
                  && taken["avatar"]?["display"]?.ToString() != "Claude", taken.ToString());
            var boss = await me.Call("agent_avatar", new JObject { ["display"] = "บอส" });
            Check("…nor the owner's title", boss["ignored"]?["display"] != null, boss.ToString());
            var own = await me.Call("agent_avatar", new JObject { ["display"] = "Codex the Builder" });
            Check("…while a name of its own is kept", own["avatar"]?["display"]?.ToString() == "Codex the Builder", own.ToString());

            var file = Path.Combine(vault, ".obsidianx", "agent-bus", "avatars", "codex.json");
            Check("saved under the caller's own name only", File.Exists(file)
                  && !File.Exists(Path.Combine(vault, ".obsidianx", "agent-bus", "avatars", "claude.json")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        static bool FarFromSkin(string hex)
        {
            if (hex.Length != 7 || hex[0] != '#') return true;
            var c = Convert.ToInt32(hex[1..], 16);
            return TestSkins.All(s =>
            {
                var k = Convert.ToInt32(s[1..], 16);
                var d = Math.Sqrt(Math.Pow(((c >> 16) & 255) - ((k >> 16) & 255), 2)
                                + Math.Pow(((c >> 8) & 255) - ((k >> 8) & 255), 2)
                                + Math.Pow((c & 255) - (k & 255), 2));
                return d >= 48;
            });
        }
    }
}
