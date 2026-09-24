using System.Text;
using BrainX.ServerManager.Backend;
using BrainX.ServerManager.Infrastructure;
using BrainX.ServerManager.UI;

namespace BrainX.ServerManager;

/// <summary>
/// Startup, in order:
/// <list type="number">
/// <item><c>--wait-pid</c>: a restart — let the previous instance exit first.</item>
/// <item><c>--screenshot</c>: render pages to PNG and exit (no mutex, no shadow copy).</item>
/// <item>Shadow copy: the published exe never runs from C:\brainx\app\manager\ (the
///   node's self-updater replaces files there); it copies itself to
///   %LOCALAPPDATA%\BrainX\ServerManager\run\ and starts that copy.</item>
/// <item>One instance per session: a second launch brings the first to the front.</item>
/// <item>Mode: service mode when the BrainXNode service exists (or <c>--demo</c>),
///   else the old dev mode that runs BrainX.Server as a child process.</item>
/// </list>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] argv)
    {
        var args = AppArgs.Parse(argv);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // schtasks prints in the OEM code page
        if (args.WaitPid is { } pid) ShadowLauncher.WaitForExit(pid);

        ApplicationConfiguration.Initialize();
        Theme.Init(args.EmulateScale);
        Application.SetDefaultFont(Theme.Body);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => OnCrash(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => OnCrash(e.ExceptionObject as Exception, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ManagerLog.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };

        bool elevated = Elevation.IsElevated();

        if (args.ScreenshotDir != null) return Screenshots.Run(args, elevated);

        if (ShadowLauncher.ShouldShadow(args))
        {
            if (SingleInstance.AnotherIsRunning())
            {
                SingleInstance.SignalFirstInstance();
                return 0;
            }
            if (ShadowLauncher.TryLaunchShadowCopy(args, elevated)) return 0;
            // Could not shadow (logged): run in place rather than not at all.
        }

        using var instance = SingleInstance.TryAcquire();
        if (instance == null)
        {
            if (!SingleInstance.SignalFirstInstance())
                MessageBox.Show("BrainX Server Manager เปิดอยู่แล้ว — ดูที่ไอคอนใน tray ข้างนาฬิกา", AppInfo.Product,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ManagerLog.FileEnabled = args.Demo == null;
        ManagerLog.Info($"start {AppInfo.Version} | elevated={elevated} | exe={Environment.ProcessPath} | origin={args.Origin ?? "-"}");

        IServerBackend? backend = null;
        bool serviceMode;
        if (args.Demo != null)
        {
            backend = new DemoServerBackend(DemoServerBackend.ParseScenario(args.Demo), fast: false);
            serviceMode = true;
        }
        else if (!args.ForceDev && WindowsServerBackend.NodeServiceExists())
        {
            backend = new WindowsServerBackend(elevated);
            serviceMode = true;
        }
        else serviceMode = false;

        try
        {
            var ctx = new ManagerContext
            {
                Backend = backend!,
                IsElevated = backend?.IsElevated ?? elevated,
                Args = args,
                State = ManagerState.Load(persist: args.Demo == null),
                TargetExe = ResolveTargetExe(args, backend),
            };
            using var form = new MainForm(ctx, serviceMode, startHidden: args.Minimized);
            instance.OnActivateRequested(() =>
            {
                try { form.BeginInvoke(form.ShowFromTray); }
                catch (InvalidOperationException) { /* closing */ }
            });
            Application.Run(form);
        }
        finally
        {
            backend?.Dispose();
            ManagerLog.Info("exit");
        }
        return 0;
    }

    /// <summary>
    /// What the Start Menu shortcut and the logon task start: the origin exe the
    /// self-updater keeps current — never the shadow copy.
    /// </summary>
    private static string ResolveTargetExe(AppArgs args, IServerBackend? backend)
    {
        if (args.Origin != null) return args.Origin;
        var self = Environment.ProcessPath ?? Application.ExecutablePath;
        if (backend != null && ShadowLauncher.IsInsideRunDir(self))
        {
            var shipped = Path.Combine(backend.Paths.AppDir, "manager", Path.GetFileName(self));
            if (File.Exists(shipped)) return shipped;
        }
        return self;
    }

    private static void OnCrash(Exception? ex, bool fatal)
    {
        ManagerLog.Error(fatal ? "fatal unhandled exception" : "unhandled UI exception", ex);
        if (fatal) return;
        try
        {
            MessageBox.Show("เกิดข้อผิดพลาดที่ไม่คาดคิด — โปรแกรมยังทำงานต่อได้\nรายละเอียดอยู่ใน " + ManagerLog.FilePath,
                AppInfo.Product, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch { /* nothing more to do */ }
    }
}
