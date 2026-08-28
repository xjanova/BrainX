// AssistantWindow — her own window: the face and the chat together.
//
// Separate from the main window because that is what she is FOR: a thing you
// leave open beside your work and talk to. Inside the dashboard she competed
// with the galaxy for the same corner, and the chat panel covered the very
// cards it was reporting on (which is what the owner's screenshot showed).
//
// Frameless, and the HTML draws its own title strip. Position, size and the
// always-on-top choice are remembered in the vault's settings.json next to
// her name and voice — a window that forgets where you put it gets closed
// and never reopened.

using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Newtonsoft.Json.Linq;

namespace BrainX.Client;

public partial class AssistantWindow : Window
{
    private readonly string _vaultPath;
    private readonly Action<string> _onAsk;
    private bool _ready;

    /// <summary>Raised when the page reports it has booted, so the host can
    /// push the name and voice in.</summary>
    public event EventHandler? PageReady;

    public AssistantWindow(string vaultPath, Action<string> onAsk)
    {
        InitializeComponent();
        _vaultPath = vaultPath;
        _onAsk = onAsk;
        RestorePlacement();
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) => SavePlacement();
    }

    private string SettingsPath => Path.Combine(_vaultPath, ".obsidianx", "settings.json");

    private JObject ReadSettings()
    {
        try { if (File.Exists(SettingsPath)) return JObject.Parse(File.ReadAllText(SettingsPath)); }
        catch { }
        return new JObject();
    }

    private void RestorePlacement()
    {
        var s = ReadSettings();
        try
        {
            var w = (double?)s["AssistantWinW"] ?? 380;
            var h = (double?)s["AssistantWinH"] ?? 640;
            Width = Math.Max(MinWidth, w);
            Height = Math.Max(MinHeight, h);

            var l = (double?)s["AssistantWinX"];
            var t = (double?)s["AssistantWinY"];
            if (l.HasValue && t.HasValue)
            {
                // A remembered position from a monitor that is now unplugged
                // would put her off-screen with no way to drag her back, so
                // anything outside the virtual desktop falls back to centred.
                double vx = SystemParameters.VirtualScreenLeft;
                double vy = SystemParameters.VirtualScreenTop;
                double vw = SystemParameters.VirtualScreenWidth;
                double vh = SystemParameters.VirtualScreenHeight;
                if (l.Value > vx - Width + 80 && l.Value < vx + vw - 80 &&
                    t.Value > vy - 20 && t.Value < vy + vh - 80)
                {
                    Left = l.Value; Top = t.Value;
                }
                else CenterOnPrimary();
            }
            else CenterOnPrimary();

            Topmost = (bool?)s["AssistantWinTopmost"] ?? false;
        }
        catch { CenterOnPrimary(); }
    }

    private void CenterOnPrimary()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = Math.Max(0, (SystemParameters.PrimaryScreenHeight - Height) / 2);
    }

    private void SavePlacement()
    {
        try
        {
            var s = ReadSettings();
            // RestoreBounds, not Left/Top: while minimised or maximised the
            // live properties report the transformed rectangle, and saving
            // that is how a window comes back the wrong size.
            var r = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            s["AssistantWinX"] = r.Left; s["AssistantWinY"] = r.Top;
            s["AssistantWinW"] = r.Width; s["AssistantWinH"] = r.Height;
            s["AssistantWinTopmost"] = Topmost;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            // BOM-free: the MCP and the hooks read this file with plain JSON
            // parsers, and a BOM here has broken a config before.
            File.WriteAllText(SettingsPath, s.ToString(Newtonsoft.Json.Formatting.Indented),
                              new UTF8Encoding(false));
        }
        catch { }
    }

    private async System.Threading.Tasks.Task InitAsync()
    {
        try
        {
            var wwwroot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            var env = await CoreWebView2Environment.CreateAsync();
            await AssistantWeb.EnsureCoreWebView2Async(env);
            var core = AssistantWeb.CoreWebView2;

            core.SetVirtualHostNameToFolderMapping(
                "universe.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

            // Her audio lives in the vault, not wwwroot — wwwroot is inside the
            // directory Velopack replaces wholesale on update.
            var voiceDir = Path.Combine(_vaultPath, ".obsidianx", "voice");
            Directory.CreateDirectory(voiceDir);
            core.SetVirtualHostNameToFolderMapping(
                "voice.local", voiceDir, CoreWebView2HostResourceAccessKind.Allow);

            // The mic is the point of this window, so the permission is granted
            // for our own virtual host rather than left to prompt inside a
            // frameless window that has nowhere sensible to show a prompt.
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Microphone &&
                    e.Uri.StartsWith("https://universe.local", StringComparison.OrdinalIgnoreCase))
                    e.State = CoreWebView2PermissionState.Allow;
            };

            core.WebMessageReceived += OnMessage;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = false;

            AssistantWeb.Source = new Uri("https://universe.local/universe/assistant-window.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Assistant window failed to start: {ex.Message}",
                            "Assistant", MessageBoxButton.OK, MessageBoxImage.Warning);
            Close();
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.WebMessageAsJson;
            var m = Newtonsoft.Json.JsonConvert.DeserializeAnonymousType(
                json, new { type = "", text = "", action = "" });
            switch (m?.type)
            {
                case "mind.ready":
                    _ready = true;
                    PageReady?.Invoke(this, EventArgs.Empty);
                    break;
                case "mind.ask":
                    if (!string.IsNullOrWhiteSpace(m.text)) _onAsk(m.text);
                    break;
                case "mind.window":
                    if (m.action == "close") Close();
                    else if (m.action == "minimize") WindowState = WindowState.Minimized;
                    break;
                case "mind.drag":
                    // The HTML drag region needs a real move; app-region works
                    // in Edge but not inside a WebView2 host, so the page asks
                    // and the window does it.
                    try { DragMove(); } catch { }
                    break;
            }
        }
        catch { }
    }

    /// <summary>Run script in the page. No-ops until it has booted.</summary>
    public async System.Threading.Tasks.Task<string?> EvalAsync(string js)
    {
        if (!_ready || AssistantWeb?.CoreWebView2 == null) return null;
        try { return await AssistantWeb.CoreWebView2.ExecuteScriptAsync(js); }
        catch { return null; }
    }

    public bool PageIsReady => _ready;
}
