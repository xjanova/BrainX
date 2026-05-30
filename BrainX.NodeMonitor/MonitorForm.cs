using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BrainX.NodeMonitor;

/// <summary>
/// Tiny control panel for the brainx-node server: Start / Stop, live status from
/// /health, one-click "Open Dashboard", a tail of the server's stdout, and a
/// "start with Windows" toggle. Minimizes to the tray.
/// </summary>
public sealed class MonitorForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "BrainXNodeMonitor";

    private readonly MonitorSettings _settings = MonitorSettings.Load();
    private readonly NodeController _node = new();
    private readonly HttpClient _http;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 2000 };

    // UI
    private readonly Panel _dot = new();
    private readonly Label _status = new();
    private readonly NumericUpDown _port = new();
    private readonly TextBox _vault = new();
    private readonly ComboBox _storage = new();
    private readonly TextBox _mysql = new();
    private readonly Button _btnStart = new();
    private readonly Button _btnStop = new();
    private readonly Button _btnDash = new();
    private readonly TextBox _log = new();
    private readonly CheckBox _startup = new();

    // Tray
    private readonly NotifyIcon _tray = new();
    private readonly Icon _icoGray, _icoGreen, _icoAmber;
    private bool _exiting;
    private bool _healthUp;

    private static readonly Color Bg = Color.FromArgb(10, 14, 26);
    private static readonly Color Card = Color.FromArgb(18, 24, 42);
    private static readonly Color Fg = Color.FromArgb(220, 230, 245);
    private static readonly Color Accent = Color.FromArgb(0, 229, 255);

    public MonitorForm()
    {
        _http = new HttpClient(new HttpClientHandler { UseProxy = false })  // WPAD auto-detect tanks localhost
        {
            Timeout = TimeSpan.FromSeconds(2)
        };
        _icoGray = DotIcon(Color.FromArgb(110, 120, 140));
        _icoGreen = DotIcon(Color.FromArgb(74, 222, 128));
        _icoAmber = DotIcon(Color.FromArgb(255, 179, 0));

        BuildUi();
        BuildTray();

        _node.Log += AppendLog;
        _poll.Tick += async (_, _) => await PollAsync();
        _poll.Start();
        _ = PollAsync();
    }

    // ───────────────────────── UI ─────────────────────────
    private void BuildUi()
    {
        Text = "BrainX Node Monitor";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(560, 520);
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9F);
        Icon = _icoGray;

        var title = new Label
        {
            Text = "BrainX Node",
            Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            ForeColor = Accent,
            AutoSize = true,
            Location = new Point(18, 14),
        };
        Controls.Add(title);

        // status row
        _dot.Size = new Size(12, 12);
        _dot.Location = new Point(20, 52);
        _dot.BackColor = Color.Transparent;
        _dot.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(_dot.Tag as Color? ?? Color.Gray);
            e.Graphics.FillEllipse(b, 0, 0, 11, 11);
        };
        _dot.Tag = Color.Gray;
        Controls.Add(_dot);

        _status.Text = "Stopped";
        _status.Font = new Font("Consolas", 10.5F);
        _status.AutoSize = true;
        _status.Location = new Point(40, 50);
        Controls.Add(_status);

        // settings card
        var lblPort = MakeLabel("PORT", 20, 88);
        Controls.Add(lblPort);
        _port.Minimum = 1024; _port.Maximum = 65535; _port.Value = Clamp(_settings.Port, 1024, 65535);
        _port.Location = new Point(20, 106); _port.Width = 90;
        StyleInput(_port);
        Controls.Add(_port);

        var lblVault = MakeLabel("VAULT PATH (blank = none)", 130, 88);
        Controls.Add(lblVault);
        _vault.Text = _settings.VaultPath;
        _vault.Location = new Point(130, 106); _vault.Width = 330;
        StyleInput(_vault);
        Controls.Add(_vault);

        var btnBrowse = new Button { Text = "…", Location = new Point(466, 105), Size = new Size(34, 26) };
        StyleButton(btnBrowse, Color.FromArgb(60, 70, 95));
        btnBrowse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog();
            if (!string.IsNullOrWhiteSpace(_vault.Text)) d.SelectedPath = _vault.Text;
            if (d.ShowDialog(this) == DialogResult.OK) _vault.Text = d.SelectedPath;
        };
        Controls.Add(btnBrowse);

        // storage backend row
        Controls.Add(MakeLabel("STORAGE", 20, 150));
        _storage.DropDownStyle = ComboBoxStyle.DropDownList;
        _storage.Items.AddRange(new object[] { "sqlite", "mysql" });
        _storage.SelectedItem = _settings.StorageProvider?.ToLowerInvariant() == "mysql" ? "mysql" : "sqlite";
        _storage.Location = new Point(20, 168); _storage.Width = 100;
        _storage.FlatStyle = FlatStyle.Flat; _storage.BackColor = Color.FromArgb(6, 9, 17); _storage.ForeColor = Fg;
        _storage.SelectedIndexChanged += (_, _) => _mysql.Enabled = (string)_storage.SelectedItem! == "mysql";
        Controls.Add(_storage);

        Controls.Add(MakeLabel("MYSQL CONNECTION STRING (mysql only)", 140, 150));
        _mysql.Text = _settings.MySqlConnString;
        _mysql.Location = new Point(140, 168); _mysql.Width = 360;
        _mysql.PlaceholderText = "Server=host;Port=3306;Database=brainx;Uid=user;Pwd=...;";
        StyleInput(_mysql);
        _mysql.Enabled = (string)_storage.SelectedItem! == "mysql";
        Controls.Add(_mysql);

        // action buttons
        _btnStart.Text = "▶  Start"; _btnStart.Location = new Point(20, 204); _btnStart.Size = new Size(150, 40);
        StyleButton(_btnStart, Color.FromArgb(34, 120, 70)); _btnStart.Click += async (_, _) => await StartAsync();
        Controls.Add(_btnStart);

        _btnStop.Text = "■  Stop"; _btnStop.Location = new Point(180, 204); _btnStop.Size = new Size(150, 40);
        StyleButton(_btnStop, Color.FromArgb(140, 50, 60)); _btnStop.Click += (_, _) => StopServer();
        Controls.Add(_btnStop);

        _btnDash.Text = "🖥  Open Dashboard"; _btnDash.Location = new Point(340, 204); _btnDash.Size = new Size(160, 40);
        StyleButton(_btnDash, Color.FromArgb(40, 90, 140)); _btnDash.Click += (_, _) => OpenDashboard();
        Controls.Add(_btnDash);

        // log
        var lblLog = MakeLabel("SERVER LOG", 20, 256);
        Controls.Add(lblLog);
        _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Vertical; _log.WordWrap = false;
        _log.Location = new Point(20, 274); _log.Size = new Size(520, 184);
        _log.BackColor = Color.FromArgb(6, 9, 17); _log.ForeColor = Color.FromArgb(150, 200, 180);
        _log.Font = new Font("Consolas", 8.5F); _log.BorderStyle = BorderStyle.FixedSingle;
        Controls.Add(_log);

        _startup.Text = "Start with Windows";
        _startup.Location = new Point(20, 468); _startup.AutoSize = true; _startup.ForeColor = Fg;
        _startup.Checked = IsStartupEnabled();
        _startup.CheckedChanged += (_, _) => SetStartup(_startup.Checked);
        Controls.Add(_startup);

        var hint = new Label
        {
            Text = "Closing hides to tray · right-click the tray icon for quick actions",
            ForeColor = Color.FromArgb(110, 125, 150), AutoSize = true,
            Font = new Font("Segoe UI", 8F), Location = new Point(190, 470),
        };
        Controls.Add(hint);
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Start", null, async (_, _) => await StartAsync());
        menu.Items.Add("Stop", null, (_, _) => StopServer());
        menu.Items.Add("Open Dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show Monitor", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });

        _tray.Icon = _icoGray;
        _tray.Text = "BrainX Node Monitor";
        _tray.Visible = true;
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    // ───────────────────────── actions ─────────────────────────
    private async Task StartAsync()
    {
        _settings.Port = (int)_port.Value;
        _settings.VaultPath = _vault.Text.Trim();
        _settings.StorageProvider = (string)_storage.SelectedItem! ?? "sqlite";
        _settings.MySqlConnString = _mysql.Text.Trim();
        _settings.Save();

        if (_settings.StorageProvider == "mysql" && string.IsNullOrWhiteSpace(_settings.MySqlConnString))
        {
            MessageBox.Show(this, "MySQL selected but no connection string set.\nThe node will fall back to SQLite.",
                "MySQL", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        if (await IsHealthyAsync())
        {
            AppendLog($"[monitor] A server is already responding on :{_settings.Port} — not launching another.");
            await PollAsync();
            return;
        }

        var path = NodeController.ResolveServerPath(_settings.ServerPathOverride);
        if (path == null)
        {
            MessageBox.Show(this,
                "Could not find BrainX.Server build output.\n\n" +
                "Build the server once (Visual Studio or `dotnet build BrainX.Server`),\n" +
                "or set an explicit path in settings.json.",
                "Server not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        AppendLog($"[monitor] Launching: {path}");
        AppendLog($"[monitor] Port :{_settings.Port}  Vault: {(string.IsNullOrWhiteSpace(_settings.VaultPath) ? "(none)" : _settings.VaultPath)}  Storage: {_settings.StorageProvider}");
        if (!_node.Start(path, _settings.Port, _settings.VaultPath, embedded: true,
                         _settings.StorageProvider, _settings.MySqlConnString, out var err))
        {
            AppendLog($"[monitor] FAILED to start: {err}");
            MessageBox.Show(this, "Failed to start server:\n\n" + err, "Start failed",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        await PollAsync();
    }

    private void StopServer()
    {
        if (!_node.OursRunning)
        {
            AppendLog("[monitor] No server launched by this monitor to stop. " +
                      "(An external server — e.g. started from Visual Studio — must be stopped there.)");
            return;
        }
        AppendLog("[monitor] Stopping server…");
        _node.Stop();
        AppendLog("[monitor] Stopped.");
        _ = PollAsync();
    }

    private void OpenDashboard()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = $"http://localhost:{(int)_port.Value}/",
                UseShellExecute = true,   // let the OS pick the default browser
            });
        }
        catch (Exception ex) { AppendLog("[monitor] Open dashboard failed: " + ex.Message); }
    }

    // ───────────────────────── health poll ─────────────────────────
    private async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var r = await _http.GetAsync($"http://localhost:{(int)_port.Value}/health");
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task PollAsync()
    {
        int port = (int)_port.Value;
        bool up = false; string? vaultFlag = null;
        try
        {
            using var r = await _http.GetAsync($"http://localhost:{port}/health");
            if (r.IsSuccessStatusCode)
            {
                up = true;
                try
                {
                    using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("vaultConfigured", out var v))
                        vaultFlag = v.GetBoolean() ? "vault ✓" : "vault ✗";
                }
                catch { /* health body not JSON — fine */ }
            }
        }
        catch { up = false; }

        _healthUp = up;
        bool ours = _node.OursRunning;

        Color dotColor;
        string text;
        Icon trayIcon;
        if (up)
        {
            dotColor = Color.FromArgb(74, 222, 128);
            trayIcon = _icoGreen;
            text = $"Running{(ours ? "" : " (external)")} · :{port}" + (vaultFlag != null ? $" · {vaultFlag}" : "");
        }
        else if (ours)
        {
            dotColor = Color.FromArgb(255, 179, 0);
            trayIcon = _icoAmber;
            text = $"Starting… · :{port}";
        }
        else
        {
            dotColor = Color.FromArgb(110, 120, 140);
            trayIcon = _icoGray;
            text = "Stopped";
        }

        _dot.Tag = dotColor; _dot.Invalidate();
        _status.Text = text;
        if (_tray.Icon != trayIcon) _tray.Icon = trayIcon;
        _tray.Text = "BrainX Node — " + text;

        _btnStart.Enabled = !up;
        _btnStop.Enabled = ours;
        _btnDash.Enabled = up;
    }

    // ───────────────────────── tray / lifecycle ─────────────────────────
    private void ShowFromTray()
    {
        Show(); WindowState = FormWindowState.Normal; Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(1500, "BrainX Node Monitor", "Still running in the tray.", ToolTipIcon.Info);
            return;
        }
        _poll.Stop();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    // ───────────────────────── helpers ─────────────────────────
    private void AppendLog(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action<string>(AppendLog), line); return; }
        if (_log.Lines.Length > 500)
            _log.Lines = _log.Lines[^300..];
        _log.AppendText(line + Environment.NewLine);
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private static Label MakeLabel(string text, int x, int y) => new()
    {
        Text = text, ForeColor = Color.FromArgb(120, 140, 170), AutoSize = true,
        Font = new Font("Segoe UI", 8F, FontStyle.Bold), Location = new Point(x, y),
    };

    private static void StyleInput(Control c)
    {
        c.BackColor = Color.FromArgb(6, 9, 17);
        c.ForeColor = Color.FromArgb(220, 230, 245);
        if (c is TextBox tb) tb.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void StyleButton(Button b, Color back)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 85, 110);
        b.BackColor = back;
        b.ForeColor = Color.White;
        b.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        b.Cursor = Cursors.Hand;
    }

    private static Icon DotIcon(Color c)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var b = new SolidBrush(c);
            g.FillEllipse(b, 2, 2, 12, 12);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static bool IsStartupEnabled()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue(RunValueName) != null; }
        catch { return false; }
    }

    private static void SetStartup(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k!.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
            else k!.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch { /* best-effort */ }
    }
}
