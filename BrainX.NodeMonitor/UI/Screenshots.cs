using System.Drawing.Imaging;
using BrainX.ServerManager.Backend;
using BrainX.ServerManager.Infrastructure;

namespace BrainX.ServerManager.UI;

/// <summary>
/// Hidden <c>--screenshot &lt;dir&gt;</c>: build the real window off-screen, load each
/// page, and render it with Control.DrawToBitmap — no GUI automation, no tray
/// icon, no timers, no dialogs. Without <c>--demo</c> it shows this machine's real
/// mode (dev mode on a box without the service). <c>--demo=all</c> renders the
/// service-mode pages for every scenario plus the confirmation dialogs.
/// Prints each written path to stdout.
/// </summary>
internal static class Screenshots
{
    public static int Run(AppArgs args, bool elevated)
    {
        var dir = Path.GetFullPath(args.ScreenshotDir!);
        Directory.CreateDirectory(dir);
        ManagerLog.FileEnabled = false;
        int exit = 0;
        var appCtx = new ApplicationContext();
        var kick = new System.Windows.Forms.Timer { Interval = 20 };
        kick.Tick += async (_, _) =>
        {
            kick.Stop();
            kick.Dispose();
            try { await RunAllAsync(args, elevated, dir); }
            catch (Exception ex)
            {
                exit = 1;
                Console.Error.WriteLine("screenshot run failed: " + ex);
            }
            finally { appCtx.ExitThread(); }
        };
        kick.Start();
        Application.Run(appCtx);
        return exit;
    }

    private static async Task RunAllAsync(AppArgs args, bool elevated, string dir)
    {
        var suffix = args.EmulateScale > 1.01f ? $"@{Math.Round(args.EmulateScale * 100)}" : "";
        if (args.Demo == null)
        {
            if (!args.ForceDev && WindowsServerBackend.NodeServiceExists())
            {
                using var real = new WindowsServerBackend(elevated);
                await CaptureAsync(real, true, args, dir, "service", null, suffix);
            }
            else await CaptureAsync(null, false, args, dir, "dev", null, suffix, elevated);
            return;
        }

        if (args.Demo != "all")
        {
            var sc = DemoServerBackend.ParseScenario(args.Demo);
            using var b = new DemoServerBackend(sc, fast: true);
            await CaptureAsync(b, true, args, dir, "demo-" + sc.ToString().ToLowerInvariant(), null, suffix);
            return;
        }

        async Task Scenario(DemoScenario sc, string prefix, string[]? pages, Func<MainForm, Task>? then = null, string? thenPage = null)
        {
            using var b = new DemoServerBackend(sc, fast: true);
            await CaptureAsync(b, true, args, dir, prefix, pages, suffix, after: then, afterPage: thenPage);
        }
        static Task Rotate(MainForm f) => ((TokenPage)f.Pages.First(p => p.Key == "token")).RunRotationAsync();

        await Scenario(DemoScenario.Normal, "demo", null);
        await Scenario(DemoScenario.Empty, "scenario-empty", ["accounts"]);
        await Scenario(DemoScenario.Stopped, "scenario-stopped", ["overview", "accounts", "log"]);
        await Scenario(DemoScenario.OldNode, "scenario-oldnode", ["overview", "accounts"]);
        await Scenario(DemoScenario.TunnelDown, "scenario-tunneldown", ["overview"]);
        await Scenario(DemoScenario.ReadOnly, "scenario-readonly", ["overview", "settings"]);
        await Scenario(DemoScenario.NoToken, "scenario-notoken", ["token", "overview"]);
        // Stuck StartPending: the state itself, then a Start that gives up with the 60 s message.
        await Scenario(DemoScenario.Pending, "scenario-pending", ["overview"],
            then: async f => await f.Context.Services.RunAsync(f.Context.Backend.NodeServiceName, ServiceAction.Start, skipConfirm: true),
            thenPage: "overview");
        // Owner token: a current node takes the rotated file token; an old node (token still in the
        // service Environment) rejects it, and the rotation is rolled back.
        await Scenario(DemoScenario.Normal, "token-rotate", ["token"], then: Rotate, thenPage: "token");
        await Scenario(DemoScenario.LegacyEnv, "scenario-legacyenv", ["settings", "token"], then: Rotate, thenPage: "token");
        if (string.IsNullOrEmpty(suffix)) CaptureDialogs(dir);
    }

    private static async Task CaptureAsync(IServerBackend? backend, bool serviceMode, AppArgs args, string dir, string prefix,
        string[]? only, string suffix, bool elevated = true, Func<MainForm, Task>? after = null, string? afterPage = null)
    {
        var ctx = new ManagerContext
        {
            Backend = backend!,
            IsElevated = backend?.IsElevated ?? elevated,
            Args = args,
            State = ManagerState.Load(persist: false),
            TargetExe = @"C:\brainx\app\manager\BrainX.ServerManager.exe",
            ScreenshotMode = true,
        };
        using var form = new MainForm(ctx, serviceMode, startHidden: false)
        {
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
        };
        form.Location = new Point(SystemInformation.VirtualScreen.Right + 400, SystemInformation.VirtualScreen.Top);
        form.Show();
        await form.PrepareForScreenshotAsync();

        int i = 0;
        foreach (var p in form.Pages)
        {
            i++;
            if (only != null && !only.Contains(p.Key)) continue;
            await form.ShowPageForCaptureAsync(p);
            Save(form, Path.Combine(dir, $"{prefix}-{i}-{p.Key}{suffix}.png"));
        }
        if (after != null)
        {
            await after(form);
            await form.PrepareForScreenshotAsync();
            var p = form.Pages.First(x => x.Key == afterPage);
            await form.ShowPageForCaptureAsync(p);
            Save(form, Path.Combine(dir, $"{prefix}-after-{p.Key}{suffix}.png"));
        }
        form.CloseForScreenshot();
    }

    private static void Save(MainForm form, string path)
    {
        using var bmp = form.CaptureImage();
        bmp.Save(path, ImageFormat.Png);
        Console.WriteLine(path);
    }

    private static void CaptureDialogs(string dir)
    {
        var acc = new CloudAccount
        {
            Id = "3f9a1c7e5b2d4f60a8e1c9b7d5f3a2e1", KeyHint = "…7K2F", LicenseType = "monthly",
            UsedBytes = 312L * 1024 * 1024, QuotaBytes = 1024L * 1024 * 1024, NoteCount = 1284, TokenCount = 2,
        };
        Dialog(new ConfirmDialog($"ลบบัญชี {acc.ShortId} ถาวร", "ลบทุกอย่างของบัญชีนี้บนเซิร์ฟเวอร์ — กู้คืนไม่ได้:", "ลบบัญชีถาวร", danger: true,
                detail: $"บัญชี   {acc.Id}\nKey     {acc.KeyHint}\nโน้ต    1,284 · พื้นที่ 312 MB\n\n• ลบ token ทุกตัว session ทั้งหมด โฟลเดอร์ vault และแถวในฐานข้อมูล\n• ถ้า license ยังไม่หมดอายุ ลูกค้าล็อกอินใหม่ได้ (จะได้พื้นที่ว่างใหม่)\n  ถ้าต้องการกันไม่ให้ใช้ ให้ “ระงับบัญชี” แทน",
                warning: "ข้อมูลโน้ตของลูกค้าบนเซิร์ฟเวอร์จะหายถาวร", typeToConfirm: acc.Id),
            Path.Combine(dir, "dialog-delete-account.png"));
        Dialog(new QuotaDialog(acc), Path.Combine(dir, "dialog-quota.png"));
        Dialog(new SelfInstallDialog(@"C:\brainx\app\manager\BrainX.ServerManager.exe"), Path.Combine(dir, "dialog-first-run.png"));
        Dialog(new ConfirmDialog("บันทึก Service Environment?", "จะเขียนการเปลี่ยนแปลงนี้ลง Registry (สำรองค่าเดิมไว้ใน manager-backups ก่อน):", "บันทึก", danger: true,
                detail: "~ BrainX__RequireAuth: true → false\n~ BrainX__CloudQuotaMb: 1024 → 2048\n+ BrainX__LogDir = C:\\brainx\\logs\n~ BrainX__McpReadToken: •••••• (ซ่อน) → •••••• (ซ่อน)",
                warning: "คุณกำลังปิด RequireAuth — ใครก็ตามที่เข้าถึง https://serverbrain.xman4289.com จะเรียก /api ได้โดยไม่ต้องมี Token รวมถึงจุดที่เขียนข้อมูล",
                typeToConfirm: "RequireAuth=false"),
            Path.Combine(dir, "dialog-save-env.png"));
        Dialog(new ConfirmDialog("หยุด BrainXNode?", "ยืนยันก่อนดำเนินการ:", "Stop BrainXNode", danger: true,
                detail: "• ลูกค้า Cloud และ MCP ที่เชื่อมต่ออยู่จะหลุดทันที\n• https://serverbrain.xman4289.com จะใช้ไม่ได้จนกว่าจะ Start ใหม่\n• Windows จะไม่เปิดให้เองจนกว่าจะรีบูต"),
            Path.Combine(dir, "dialog-stop-node.png"));
        Dialog(new ConfirmDialog(TokenPage.ConfirmTitle, TokenPage.ConfirmMessage, "เปลี่ยน Token", danger: true,
                detail: TokenPage.ConfirmDetail(@"C:\brainx\bearer-token.txt", legacyEnvLine: true, mcpWriteTokenSet: false)),
            Path.Combine(dir, "dialog-rotate-token.png"));
        Dialog(new ConfirmDialog(TokenPage.ReminderTitle, TokenPage.ReminderMessage, "คัดลอก Token ใหม่", danger: false,
                detail: TokenPage.TokenReminder, cancelText: "ปิด"),
            Path.Combine(dir, "dialog-token-rotated.png"));
    }

    private static void Dialog(Form d, string path)
    {
        using (d)
        {
            d.StartPosition = FormStartPosition.Manual;
            d.ShowInTaskbar = false;
            d.Location = new Point(SystemInformation.VirtualScreen.Right + 400, SystemInformation.VirtualScreen.Top);
            d.Show();
            Application.DoEvents();
            var client = d.ClientSize;
            using var bmp = new Bitmap(client.Width, client.Height);
            // The client area only: WM_PRINT paints a top-level frame in the pre-DWM style.
            using (var g = Graphics.FromImage(bmp)) g.Clear(d.BackColor);
            foreach (Control c in d.Controls)
                c.DrawToBitmap(bmp, new Rectangle(c.Location, c.Size));
            bmp.Save(path, ImageFormat.Png);
            Console.WriteLine(path);
            d.Close();
        }
    }
}
