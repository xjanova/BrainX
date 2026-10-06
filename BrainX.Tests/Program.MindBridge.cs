using System.Text;
using BrainX.Core.Services.Cloud;

namespace BrainX.Tests;

/// <summary>
/// One Mind on two devices (MindCloudBridge): her PC talks go up, the GigGok
/// phone app's talks come down, through the real /api/cloud routes on a test
/// node. Owner: "จะคุยเรื่องเดียวกันจำได้หมด".
/// </summary>
internal static partial class Program
{
    private static void RegisterMindBridgeChecks(List<(string Name, Func<Task> Check)> checks)
    {
        checks.Add(("mind bridge: a talk is written in the day-file shape the phone app reads", MindBridgeRecordShape));
        checks.Add(("mind bridge: phone notes come down byte-exact, PC notes go up, a second sync moves nothing", MindBridgeRoundTrip));
        checks.Add(("mind bridge: only the phone's own files come down; nothing else, nothing old", MindBridgeOnlyPhoneOwned));
        checks.Add(("mind bridge: no sign-in or no network is an answer from this PC, never a throw", MindBridgeNoSignIn));
    }

    private static string TempVault()
    {
        var v = Path.Combine(Path.GetTempPath(), "brainx-mindbridge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(v);
        return v;
    }

    private static Task MindBridgeRecordShape()
    {
        var vault = TempVault();
        try
        {
            var at = new DateTime(2026, 10, 6, 21, 5, 0);
            var bridge = new MindCloudBridge(vault, () => null, () => at);
            bridge.Record("พรุ่งนี้ไปเชียงใหม่นะ", "ได้ค่ะ\nเดินทางปลอดภัยนะคะ", "มาย");
            bridge.Record("ขอบใจ", "ยินดีค่ะ", "มาย");

            var file = Path.Combine(vault, "Mind", "Conversations", "2026-10-06 pc.md");
            Check("one file per day, named for the phone's reader", File.Exists(file));
            var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Check("no BOM (the sha must match the phone's)", !text.StartsWith('﻿'));
            Check("header once", text.Split("# 2026-10-06").Length == 2, text);
            Check("owner line", text.Contains("- 21:05 **Owner:** พรุ่งนี้ไปเชียงใหม่นะ\n"), text);
            Check("her line, second line of the message indented two spaces",
                  text.Contains("- 21:05 **Mind:** ได้ค่ะ\n  เดินทางปลอดภัยนะคะ\n"), text);
            Check("the next exchange appends", text.Contains("**Owner:** ขอบใจ"), text);
        }
        finally { try { Directory.Delete(vault, true); } catch { } }
        return Task.CompletedTask;
    }

    private static async Task MindBridgeRoundTrip()
    {
        await using var node = await CloudNode.StartAsync();
        const string key = "MIND-0000-0001";
        var token = await node.NewAccountAsync(key);
        var vault = TempVault();
        try
        {
            var today = DateTime.Now;
            var phoneDay = "Mind/Conversations/" + today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " phone.md";
            (string, string)[] phone =
            [
                ("Mind/Phone/memory.md", "# Mind's memory — phone\n\n- [fact] เจ้าของแพ้กุ้ง 📌\n"),
                ("Mind/Phone/relationship.md", "# Relationship — phone\n\n```json\n{\"together\": true}\n```\n"),
                (phoneDay, "# day\n\n- 09:05 **Owner:** คิดถึงจัง\n- 09:06 **Mind:** คิดถึงเหมือนกันค่ะ\n"),
            ];
            var up = await node.Post("/api/cloud/notes", Files(phone), token);
            Check("the phone uploaded its notes", up.Status == System.Net.HttpStatusCode.OK, up.ToString());

            var bridge = new MindCloudBridge(vault,
                () => new CloudApiClient(token, node.Http.BaseAddress!.ToString().TrimEnd('/')),
                () => today);
            bridge.Record("วันนี้คุยกับมายในมือถือด้วยนะ", "จำได้ค่ะ คุณบอกว่าคิดถึง", "มาย");
            Directory.CreateDirectory(Path.Combine(vault, "Mind"));
            File.WriteAllText(Path.Combine(vault, "Mind", "owner-profile.md"), "# สิ่งที่สังเกตเห็น\n- ชอบกาแฟดำ\n", new UTF8Encoding(false));

            var r = await bridge.SyncAsync();
            Check("sync ran", r.Error == null, r.Error ?? "");
            Check("three phone notes came down", r.Pulled == 3, r.ToString());
            var exact = phone.All(p =>
            {
                var f = Path.Combine(vault, p.Item1.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(f) && File.ReadAllBytes(f).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(p.Item2));
            });
            Check("…byte for byte, so her retrieval reads exactly what the phone wrote", exact);

            var manifest = ManifestFiles(await node.Get("/api/cloud/manifest", token)).Select(f => f["path"]?.ToString()).ToList();
            Check("her day file and the owner profile went up",
                  manifest.Contains(MindCloudBridge.PcDayPath(today)) && manifest.Contains(MindCloudBridge.OwnerProfilePath),
                  string.Join(", ", manifest));
            Check("two notes pushed", r.Pushed == 2, r.ToString());

            var again = await bridge.SyncAsync();
            Check("a second sync moves nothing", again.Pulled == 0 && again.Pushed == 0 && again.Error == null, again.ToString());

            bridge.Record("อีกเรื่อง", "ค่ะ", "มาย");
            var push = await bridge.PushTodayAsync();
            var fetched = await node.Post("/api/cloud/notes/fetch", new { paths = new[] { MindCloudBridge.PcDayPath(today) } }, token);
            Check("after an answer, today's file goes up without a manifest round trip",
                  push.Error == null && (fetched.Body["files"]?[0]?["content"]?.ToString() ?? "").Contains("อีกเรื่อง"), fetched.ToString());
        }
        finally { try { Directory.Delete(vault, true); } catch { } }
    }

    private static async Task MindBridgeOnlyPhoneOwned()
    {
        await using var node = await CloudNode.StartAsync();
        const string key = "MIND-0000-0002";
        var token = await node.NewAccountAsync(key);
        var vault = TempVault();
        try
        {
            var today = DateTime.Now;
            var old = "Mind/Conversations/" + today.AddDays(-90).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + " phone.md";
            await node.Post("/api/cloud/notes", Files(
                ("Programming/someone-elses.md", "x"),
                ("Mind/owner-profile.md", "edited somewhere else"),
                ("Mind/Phone/deeper/nested.md", "x"),
                (old, "old day")), token);

            var bridge = new MindCloudBridge(vault,
                () => new CloudApiClient(token, node.Http.BaseAddress!.ToString().TrimEnd('/')),
                () => today);
            var r = await bridge.SyncAsync();
            Check("nothing outside the phone's own files came down", r.Pulled == 0, r.ToString());
            Check("…not other notes", !File.Exists(Path.Combine(vault, "Programming", "someone-elses.md")));
            Check("…not the PC-owned profile (the PC never takes its own file from the cloud)",
                  !File.Exists(Path.Combine(vault, "Mind", "owner-profile.md")));
            Check("…not a nested folder", !Directory.Exists(Path.Combine(vault, "Mind", "Phone", "deeper")));
            Check("…not a phone day older than the window", !File.Exists(Path.Combine(vault, old.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally { try { Directory.Delete(vault, true); } catch { } }
    }

    private static async Task MindBridgeNoSignIn()
    {
        var vault = TempVault();
        try
        {
            var none = new MindCloudBridge(vault, () => null);
            var r = await none.SyncAsync();
            Check("not signed in → a result, not an exception", r.Error == "not-signed-in", r.ToString());
            var p = await none.PushTodayAsync();
            Check("…for the push too", p.Error == "not-signed-in", p.ToString());

            // A server that is not there: the port is closed straight after binding.
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            var offline = new MindCloudBridge(vault,
                () => new CloudApiClient("bxc_x", $"http://127.0.0.1:{port}", timeout: TimeSpan.FromSeconds(2)));
            var o = await offline.SyncAsync();
            Check("no network → a result with a reason", o.Error != null && o.Pulled == 0, o.ToString());
        }
        finally { try { Directory.Delete(vault, true); } catch { } }
    }
}
