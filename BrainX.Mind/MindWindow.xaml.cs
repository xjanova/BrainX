// MindWindow — the whole application.
//
// She is her own program on purpose. Inside the dashboard she competed with
// the galaxy for a corner, her chat covered the cards it was reporting on, and
// closing the dashboard closed her. This exe launches on its own, does not
// need BrainX.Client running, and keeps its own config file — the dashboard
// rewrites <vault>/.obsidianx/settings.json by serialising the ten keys IT
// knows over the whole file, which silently deleted every assistant setting
// and is exactly why "open at startup" never opened anything.

using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BrainX.Core.Services;
using Microsoft.Web.WebView2.Core;

namespace BrainX.Mind;

public partial class MindWindow : Window
{
    // Moving a frameless window that hosts a WebView2.
    //
    // Window.DragMove() cannot do it. The page can only tell us "the strip was
    // pressed" by posting a message, which arrives asynchronously — and by then
    // WPF no longer believes a mouse button is down on this window, because the
    // press landed on the WebView2's own child HWND in another process. It
    // throws, the catch swallows it, and the window simply never moves.
    //
    // Handing the press to the window manager as a caption click works because
    // the OS runs its own modal move loop off the physical button state, which
    // is still down — it never consults WPF's opinion at all.
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(
        IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;
    private const int VK_LBUTTON = 0x01;

    // The pale line across the top edge.
    //
    // WindowStyle="None" removes WPF's chrome but not the frame DWM draws
    // around every window, and on Windows 11 that frame is light — so a dark
    // frameless window wears a white hairline along its top that no amount of
    // XAML can reach, because nothing inside the window paints it. These
    // attributes are the only way to say "no border" and "this window is
    // dark"; both are no-ops on older Windows, which is why the return value
    // is ignored rather than checked.
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    private AssistantService _svc = null!;
    private AssistantConfig _cfg = new();
    private string _vault = "";
    private bool _ready;

    public MindWindow()
    {
        InitializeComponent();
        ResolvePaths();
        _cfg = _svc.LoadConfig();
        // Write it back immediately so the file EXISTS on first run. Saving
        // only on close means a crash — or a first look before the first
        // close — leaves the owner with no file to edit and no way to see
        // what is configurable.
        _svc.SaveConfig(_cfg);
        RestorePlacement();
        // SourceInitialized, not Loaded: the HWND has to exist before DWM will
        // take an attribute for it, and Loaded is too late to stop the light
        // frame being drawn once first.
        SourceInitialized += (_, _) => StripSystemBorder();
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) => SavePlacement();
    }

    private void StripSystemBorder()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h == IntPtr.Zero) return;
            int none = DWMWA_COLOR_NONE, dark = 1;
            DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref none, sizeof(int));
            DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        }
        catch { }
    }

    /// <summary>
    /// Find the vault and the MCP binary without the dashboard's help.
    ///
    /// Order matters: an explicit argument, then the env var the rest of the
    /// stack already honours, then the pointer the installed client leaves
    /// behind, then the conventional path. A standalone app that can only find
    /// its data when another app is installed is not standalone.
    /// </summary>
    private void ResolvePaths()
    {
        var args = Environment.GetCommandLineArgs();
        string? v = args.Length > 1 && Directory.Exists(args[1]) ? args[1] : null;
        v ??= Environment.GetEnvironmentVariable("BRAINX_VAULT") is { Length: > 0 } e && Directory.Exists(e) ? e : null;
        if (v == null)
        {
            try
            {
                var pointer = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BrainX", "machine-settings.json");
                if (File.Exists(pointer))
                {
                    var o = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(pointer));
                    var p = o["VaultPath"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) v = p;
                }
            }
            catch { }
        }
        v ??= @"G:\Obsidian";
        _vault = v;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var mcp = new[]
        {
            Path.Combine(local, "BrainX", "mcp", "brainx-mcp.exe"),
            Path.Combine(local, "BrainX", "current", "mcp", "brainx-mcp.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "brainx-mcp.exe"),
        }.FirstOrDefault(File.Exists) ?? "";

        _svc = new AssistantService(_vault, mcp);
    }

    private void RestorePlacement()
    {
        Width = Math.Max(MinWidth, _cfg.W);
        Height = Math.Max(MinHeight, _cfg.H);
        Topmost = _cfg.Topmost;

        if (double.IsNaN(_cfg.X) || double.IsNaN(_cfg.Y)) { Centre(); return; }
        // A position remembered from a monitor that is now unplugged would put
        // her off-screen with no way to drag her back.
        double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
        if (_cfg.X > vx - Width + 80 && _cfg.X < vx + vw - 80 &&
            _cfg.Y > vy - 20 && _cfg.Y < vy + vh - 80)
        { Left = _cfg.X; Top = _cfg.Y; }
        else Centre();
    }

    private void Centre()
    {
        Left = SystemParameters.PrimaryScreenWidth - Width - 40;
        Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - Height) / 2);
    }

    private void SavePlacement()
    {
        // RestoreBounds, not Left/Top: while minimised or maximised the live
        // properties report the transformed rectangle, and saving that is how
        // a window comes back the wrong size.
        var r = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        _cfg.X = r.Left; _cfg.Y = r.Top; _cfg.W = r.Width; _cfg.H = r.Height;
        _cfg.Topmost = Topmost;
        _svc.SaveConfig(_cfg);
    }

    private async Task InitAsync()
    {
        try
        {
            var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            // Her own user-data folder: sharing the dashboard's would make the
            // two apps fight over the same WebView2 profile lock.
            var udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BrainX", "MindWebView");
            Directory.CreateDirectory(udf);

            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: udf);
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;

            core.SetVirtualHostNameToFolderMapping(
                "universe.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

            var voiceDir = Path.Combine(_vault, ".obsidianx", "voice");
            Directory.CreateDirectory(voiceDir);
            core.SetVirtualHostNameToFolderMapping(
                "voice.local", voiceDir, CoreWebView2HostResourceAccessKind.Allow);

            // The mic is the point of this app, and a frameless window has
            // nowhere sensible to show a permission prompt.
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Microphone &&
                    e.Uri.StartsWith("https://universe.local", StringComparison.OrdinalIgnoreCase))
                    e.State = CoreWebView2PermissionState.Allow;
            };

            core.WebMessageReceived += OnMessage;
            core.Settings.AreDefaultContextMenusEnabled = false;
            Web.Source = new Uri("https://universe.local/universe/assistant-window.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Mind could not start: {ex.Message}", "Mind",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var m = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(
                e.WebMessageAsJson, new { type = "", text = "", action = "" });
            switch (m?.type)
            {
                case "mind.ready":
                    _ready = true;
                    // The page writes Thai of its own (greeting, mic errors),
                    // so it needs the particle too — not just the face gender.
                    await Eval($"window.brainxAssistant.configure({{" +
                               $"name:{Json(_cfg.Name)}," +
                               $"female:{(_cfg.Female ? "true" : "false")}," +
                               $"self:{Json(_cfg.SelfWord)}," +
                               $"particle:{Json(_cfg.EndParticle)}}})");
                    _ = _svc.WarmAsync();      // see AssistantService.WarmAsync
                    break;

                case "mind.ask":
                    if (!string.IsNullOrWhiteSpace(m.text)) _ = AskAsync(m.text);
                    break;

                case "mind.window":
                    if (m.action == "close") Close();
                    else if (m.action == "minimize") WindowState = WindowState.Minimized;
                    break;

                case "mind.drag":
                    // `-webkit-app-region: drag` is a browser/PWA feature and
                    // does not move a WebView2's host window, so the page asks.
                    // See the WM_NCLBUTTONDOWN note at the top for why this is
                    // not DragMove().
                    try
                    {
                        // Only if the button is STILL down. The page posts on
                        // mousedown and the message crosses a process boundary,
                        // so a quick click can land here after mouseup — and
                        // starting a caption drag with no button held leaves the
                        // window stuck to the cursor until the next click.
                        var h = new WindowInteropHelper(this).Handle;
                        if (h != IntPtr.Zero && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0)
                        {
                            ReleaseCapture();
                            // Blocks for the length of the OS move loop, so
                            // this returns exactly when the drag ends.
                            SendMessage(h, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
                            SavePlacement();   // survive a kill, not just a clean close
                        }
                    }
                    catch { }
                    break;
            }
        }
        catch { }
    }

    private async Task AskAsync(string question)
    {
        try
        {
            var answer = await _svc.AskAsync(question);
            if (string.IsNullOrWhiteSpace(answer))
            {
                // Her own words, so they carry her own particle. A male voice
                // apologising with ค่ะ is the wrong person talking.
                await Eval($"window.brainxChat.reply({Json($"ยังตอบไม่ได้{_cfg.EndParticle} — ตรวจว่า Ollama เปิดอยู่ที่ 11434")},false)");
                return;
            }
            // Text first, voice second: reading is faster than listening, and
            // an answer that exists only as audio cannot be re-read or copied.
            await Eval($"window.brainxChat.reply({Json(answer)},true)");

            var mp3 = await _svc.SpeakAsync(answer);
            if (mp3 != null)
                await Eval($"window.brainxAssistant.say('https://voice.local/{Uri.EscapeDataString(mp3)}')");
        }
        catch (Exception ex)
        {
            await Eval($"window.brainxChat.reply({Json($"ผิดพลาด{_cfg.EndParticle}: " + ex.Message)},false)");
        }
    }

    private async Task Eval(string js)
    {
        if (!_ready || Web?.CoreWebView2 == null) return;
        try { await Web.CoreWebView2.ExecuteScriptAsync(js); } catch { }
    }

    /// <summary>JSON-encode for a script string — a name or an answer with a
    /// quote in it would otherwise be a syntax error in the page.</summary>
    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}
