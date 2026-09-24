using System.Text.Json;
using BrainX.ServerManager.Backend;
using BrainX.ServerManager.Dev;
using BrainX.ServerManager.Infrastructure;
using Microsoft.Win32;

namespace BrainX.ServerManager.UI;

/// <summary>
/// DEV MODE (no BrainXNode service on this machine): the old Node Monitor —
/// start/stop BrainX.Server from the repo's build output as a hidden child
/// process, poll /health, tail its output, open the dashboard.
///
/// Kept from the old tool on purpose: bind localhost (0.0.0.0 gave no /health
/// on a Windows box and needs admin + a firewall prompt), drained child pipes (a
/// full 4 KB pipe freezes the child), HttpClient with UseProxy=false (WPAD
/// auto-detect adds seconds to every localhost call).
/// </summary>
internal sealed class DevPage : UserControl, IManagerPage
{
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyRunValue = "BrainXNodeMonitor";

    private readonly ManagerContext _ctx;
    private readonly MonitorSettings _settings = MonitorSettings.Load();
    private readonly NodeController _node = new();
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(2) }) { Timeout = TimeSpan.FromSeconds(3) };

    private readonly StatusDot _dot = new();
    private readonly Label _status;
    private readonly NumericUpDown _port;
    private readonly TextBox _vault, _mysql, _log;
    private readonly ComboBox _storage;
    private readonly FlatButton _btnStart, _btnStop, _btnDash;
    private readonly CheckBox _logon;
    private readonly Label _logonNote;
    private bool _polling, _starting, _logonBusy;

    public bool HealthUp { get; private set; }
    public bool OursRunning => _node.OursRunning;
    public event Action? StatusChanged;

    public string Key => "dev";
    public string Title => "Dev node";
    public string Glyph => "";
    public Control View => this;

    public DevPage(ManagerContext ctx)
    {
        _ctx = ctx;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;

        var top = new Card("BrainX Node (dev)", _dot);
        top.Add(Theme.Text("โหมด Dev: เครื่องนี้ไม่มี Service BrainXNode — Server Manager จึงเปิด BrainX.Server จากโฟลเดอร์ build เป็นโปรเซสลูก (bind localhost)",
            Theme.Small, Theme.Muted, wrap: true));
        _status = top.Add(Theme.Text("Stopped", Theme.Mono, Theme.Fg));

        var fields = new TableLayoutPanel
        {
            ColumnCount = 4, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.Card, Margin = new Padding(0, 8, 0, 0),
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label L(string t) { var l = Theme.Text(t, Theme.SmallBold, Theme.Muted); l.Margin = new Padding(0, 7, 12, 0); return l; }

        _port = new NumericUpDown { Minimum = 1024, Maximum = 65535, Value = Math.Clamp(_settings.Port, 1024, 65535), Width = 100, Margin = new Padding(0, 2, 6, 2) };
        Theme.StyleInput(_port);
        _vault = Theme.TextInput();
        _vault.Text = _settings.VaultPath;
        _vault.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _vault.PlaceholderText = "(ว่าง = ไม่มี vault)";
        var browse = Theme.Button("…", Theme.BtnGray, (_, _) =>
        {
            using var d = new FolderBrowserDialog();
            if (Directory.Exists(_vault.Text)) d.InitialDirectory = _vault.Text;
            if (d.ShowDialog(_ctx.Owner) == DialogResult.OK) _vault.Text = d.SelectedPath;
        });
        _storage = Theme.Combo(110);
        _storage.Items.AddRange(["sqlite", "mysql"]);
        _storage.SelectedItem = string.Equals(_settings.StorageProvider, "mysql", StringComparison.OrdinalIgnoreCase) ? "mysql" : "sqlite";
        _mysql = Theme.TextInput();
        _mysql.Text = _settings.MySqlConnString;
        _mysql.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _mysql.PlaceholderText = "Server=host;Port=3306;Database=brainx;Uid=user;Pwd=...;";
        _mysql.Enabled = (string?)_storage.SelectedItem == "mysql";
        _storage.SelectedIndexChanged += (_, _) => _mysql.Enabled = (string?)_storage.SelectedItem == "mysql";

        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(L("PORT"), 0, 0);
        fields.Controls.Add(_port, 1, 0);
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(L("VAULT"), 0, 1);
        fields.Controls.Add(_vault, 1, 1);
        fields.Controls.Add(browse, 2, 1);
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(L("STORAGE"), 0, 2);
        fields.Controls.Add(_storage, 1, 2);
        fields.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fields.Controls.Add(L("MYSQL"), 0, 3);
        fields.Controls.Add(_mysql, 1, 3);
        fields.RowCount = 4;
        top.Add(fields);

        _btnStart = Theme.Button("▶  Start", Theme.BtnGreen, async (_, _) => await StartAsync());
        _btnStop = Theme.Button("■  Stop", Theme.BtnRed, (_, _) => StopServer());
        _btnDash = Theme.Button("เปิดแดชบอร์ด", Theme.BtnBlue, (_, _) => Shell.Open($"http://localhost:{(int)_port.Value}/"));
        top.Add(Theme.Row(_btnStart, _btnStop, _btnDash));
        _logon = Theme.Check("เปิดอัตโนมัติเมื่อ logon (Scheduled Task — ต้องใช้สิทธิ์ Administrator)");
        _logon.Enabled = false;   // until OnShownAsync has asked schtasks (off the UI thread)
        _logon.CheckedChanged += async (_, _) => await ApplyLogonAsync();
        top.Add(_logon);
        _logonNote = top.Add(Theme.Text("", Theme.Small, Theme.Muted, wrap: true));
        top.Dock = DockStyle.Top;

        var logCard = new Card("SERVER LOG") { Dock = DockStyle.Fill };
        _log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = false, Height = 220,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom,
            BackColor = Theme.Input, ForeColor = Color.FromArgb(150, 200, 180), Font = Theme.Mono, BorderStyle = BorderStyle.FixedSingle,
        };
        logCard.Add(_log);

        var spacer = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Theme.Bg };
        Controls.Add(logCard);
        Controls.Add(spacer);
        Controls.Add(top);

        _node.Log += AppendLog;
        if (!ctx.IsElevated) _logonNote.Text = "เปิดโปรแกรมแบบ Administrator เพื่อเปลี่ยนการเปิดอัตโนมัติ";
        Render();
    }

    private bool _logonLoaded;

    public async Task OnShownAsync()
    {
        if (!_logonLoaded)
        {
            _logonLoaded = true;
            var exists = await Task.Run(AutoStart.TaskExists);
            if (!_ctx.Alive) return;
            _logonBusy = true;
            _logon.Checked = exists;
            _logonBusy = false;
            _logon.Enabled = _ctx.IsElevated;
        }
        await PollAsync();
    }

    public void OnTick(long nowMs) { }
    public bool CanLeave() => true;
    public Task PrepareScreenshotAsync() => OnShownAsync();

    // ───────────────────────── actions ─────────────────────────

    private async Task StartAsync()
    {
        if (_starting) return;   // double-click
        _starting = true;
        Render();
        try
        {
            _settings.Port = (int)_port.Value;
            _settings.VaultPath = _vault.Text.Trim();
            _settings.StorageProvider = (string?)_storage.SelectedItem ?? "sqlite";
            _settings.MySqlConnString = _mysql.Text.Trim();
            _settings.Save();

            if (_settings.StorageProvider == "mysql" && string.IsNullOrWhiteSpace(_settings.MySqlConnString))
                _ctx.Notify("เลือก MySQL แต่ไม่มี connection string — node จะใช้ SQLite แทน", true);

            if (await IsHealthyAsync())
            {
                AppendLog($"[manager] A server is already responding on :{_settings.Port} — not launching another.");
                await PollAsync();
                return;
            }

            var path = NodeController.ResolveServerPath(_settings.ServerPathOverride);
            if (path == null)
            {
                _ctx.ShowError("ไม่พบ BrainX.Server",
                    "ยังไม่มีไฟล์ build ของ BrainX.Server\n\nbuild ก่อน (Visual Studio หรือ `dotnet build BrainX.Server`) หรือกำหนด ServerPathOverride ใน settings.json");
                return;
            }
            AppendLog($"[manager] Launching: {path}");
            AppendLog($"[manager] Port :{_settings.Port}  Vault: {(string.IsNullOrWhiteSpace(_settings.VaultPath) ? "(none)" : _settings.VaultPath)}  Storage: {_settings.StorageProvider}");
            if (!_node.Start(path, _settings.Port, _settings.VaultPath, embedded: true, _settings.StorageProvider, _settings.MySqlConnString, out var err))
            {
                ManagerLog.Warn("dev server start failed: " + err);
                AppendLog("[manager] FAILED to start (details in manager.log)");
                _ctx.ShowError("เปิด server ไม่สำเร็จ", "ดูรายละเอียดใน manager.log");
            }
            await PollAsync();
        }
        finally
        {
            _starting = false;
            if (_ctx.Alive) Render();
        }
    }

    public void StopServer()
    {
        if (!_node.OursRunning)
        {
            AppendLog("[manager] No server launched from here to stop. (An external one — e.g. started from Visual Studio — must be stopped there.)");
            return;
        }
        AppendLog("[manager] Stopping server…");
        _node.Stop();
        AppendLog("[manager] Stopped.");
        _ = PollAsync();
    }

    public Task StartFromTrayAsync() => StartAsync();

    public void OpenDashboard() => Shell.Open($"http://localhost:{(int)_port.Value}/");

    private async Task ApplyLogonAsync()
    {
        if (_logonBusy) return;
        _logonBusy = true;
        var want = _logon.Checked;
        var exe = _ctx.TargetExe;
        var r = await Task.Run(() => AutoStart.ApplyTask(exe, want));
        if (r.Ok) RemoveLegacyRunKey();
        _logonBusy = false;
        if (!_ctx.Alive) return;
        _logonNote.Text = r.Message;
        _logonNote.ForeColor = r.Ok ? Theme.Muted : Theme.Bad;
        if (!r.Ok)
        {
            _logonBusy = true;
            _logon.Checked = AutoStart.TaskExists();
            _logonBusy = false;
        }
    }

    /// <summary>The old monitor's HKCU Run entry cannot start a requireAdministrator exe; the task replaces it.</summary>
    private static void RemoveLegacyRunKey()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
            k?.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
        }
        catch { /* best-effort */ }
    }

    // ───────────────────────── health poll ─────────────────────────

    private async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var r = await _http.GetAsync($"http://localhost:{(int)_port.Value}/health", _ctx.Life);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            int port = (int)_port.Value;
            bool up = false;
            string? vaultFlag = null;
            try
            {
                using var r = await _http.GetAsync($"http://localhost:{port}/health", _ctx.Life);
                if (r.IsSuccessStatusCode)
                {
                    up = true;
                    try
                    {
                        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(_ctx.Life));
                        if (doc.RootElement.Bool("vaultConfigured") is { } v) vaultFlag = v ? "vault ✓" : "vault ✗";
                    }
                    catch (JsonException) { /* not JSON: fine */ }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { up = false; }
            if (!_ctx.Alive) return;
            HealthUp = up;
            bool ours = _node.OursRunning;
            _status.Text = up ? $"Running{(ours ? "" : " (external)")} · :{port}" + (vaultFlag != null ? $" · {vaultFlag}" : "")
                : ours ? $"Starting… · :{port}" : "Stopped";
            _dot.DotColor = up ? Theme.Ok : ours ? Theme.Warn : Theme.Unknown;
            Render();
            StatusChanged?.Invoke();
        }
        finally { _polling = false; }
    }

    private void Render()
    {
        _btnStart.Enabled = !HealthUp && !_starting;
        _btnStop.Enabled = _node.OursRunning;
        _btnDash.Enabled = HealthUp;
    }

    private void AppendLog(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new Action<string>(AppendLog), line); } catch (InvalidOperationException) { }
            return;
        }
        if (_log.Lines.Length > 500) _log.Lines = _log.Lines[^300..];
        _log.AppendText(line + Environment.NewLine);
    }

    /// <summary>Exit with our child still running: stop it (its stdout pipe would break anyway).</summary>
    public void StopOwnedChild()
    {
        if (_node.OursRunning) _node.Stop();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _node.Log -= AppendLog;
            _node.Dispose();
            _http.Dispose();
        }
        base.Dispose(disposing);
    }
}
