using System.Globalization;
using BrainX.ServerManager.Backend;
using BrainX.ServerManager.Infrastructure;

namespace BrainX.ServerManager.UI;

/// <summary>
/// ตั้งค่า: the BrainXNode service Environment with real controls for the known
/// keys, an Advanced grid for every other KEY=VALUE line, the Start Menu
/// shortcut / logon task, and where things live on disk.
///
/// Saving: validate → show the change list (secrets masked) → the backend backs
/// up the current value to C:\brainx\manager-backups\env-*.txt and writes, but
/// refuses if the registry changed since this page loaded → offer a restart.
/// Lines this page does not understand survive untouched (EnvDocument.Compose).
/// The page never reloads by itself, so it cannot clobber edits in progress.
/// </summary>
internal sealed class SettingsPage : UserControl, IManagerPage
{
    private const string SecretMask = "••••••••";
    private const string RequireAuthKey = "BrainX__RequireAuth";
    private const string EmbeddedKey = "BrainX__EmbeddedMode";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private sealed class FieldEditor
    {
        public required EnvField Field { get; init; }
        public required Control Input { get; init; }
        public required Label Note { get; init; }
        public FlatButton? Extra { get; set; }
        public string OriginalEffective { get; set; } = "";
        public string? Pending { get; set; }
        public string LoadNote { get; set; } = "";
    }

    private sealed record AdvTag(int Index, string Key, string Value);

    private readonly ManagerContext _ctx;
    private readonly Dictionary<string, FieldEditor> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly DataGridView _adv;
    private readonly FlatButton _btnSave, _btnRevert, _btnReload, _btnAddRow, _btnDelRow, _btnApplyAuto;
    private readonly Label _dirty, _envError, _authWarn, _autoStatus, _pLogDir;
    private readonly CheckBox _chkShortcut, _chkTask;
    private List<string> _original = [];
    private bool _loading, _loaded, _saving, _autoBusy, _wrongKind;

    public string Key => "settings";
    public string Title => "ตั้งค่า";
    public string Glyph => "\uE713";
    public Control View => this;

    public bool HasUnsavedChanges => _loaded && IsDirty();

    public SettingsPage(ManagerContext ctx)
    {
        _ctx = ctx;
        var b = ctx.Backend;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        AutoScroll = true;

        var stack = new TableLayoutPanel
        {
            ColumnCount = 1, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Bg, Margin = Padding.Empty,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Stack(Control c) { stack.RowStyles.Add(new RowStyle(SizeType.AutoSize)); stack.Controls.Add(c, 0, stack.RowCount++); }

        // ── Service Environment ──
        var env = new Card("Service Environment");
        env.Add(Theme.Text(@"HKLM\SYSTEM\CurrentControlSet\Services\" + b.NodeServiceName + @"\Environment · REG_MULTI_SZ · มีผลหลังรีสตาร์ท Service",
            Theme.Small, Theme.Muted, wrap: true));
        _envError = env.Add(Theme.Text("", Theme.Bold, Theme.Warn, wrap: true));
        _btnSave = Theme.Button("บันทึก…", Theme.BtnGreen, async (_, _) => await SaveAsync());
        _btnRevert = Theme.Button("ยกเลิกการแก้ไข", Theme.BtnGray, async (_, _) => await RevertAsync());
        _btnReload = Theme.Button("โหลดใหม่", Theme.BtnGray, async (_, _) => await ReloadClickedAsync());
        _dirty = Theme.Text("", Theme.Bold, Theme.Muted);
        _dirty.Margin = new Padding(4, 7, 0, 0);
        env.Add(Theme.Row(_btnSave, _btnRevert, _btnReload, _dirty));

        var table = new TableLayoutPanel
        {
            ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right, BackColor = Theme.Card, Margin = new Padding(0, 8, 0, 0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var f in EnvDocument.Fields) AddField(table, f);
        env.Add(table);
        _authWarn = env.Add(Theme.Text("", Theme.Bold, Theme.Bad, wrap: true));
        Stack(env);

        // ── Advanced ──
        var adv = new Card("Advanced — ค่าอื่นใน Environment");
        adv.Add(Theme.Text("บรรทัด KEY=VALUE ที่ไม่มีช่องด้านบน (Token เจ้าของจัดการที่แท็บ Token) · ค่าลับแสดงเป็น •••• — พิมพ์ทับเพื่อเปลี่ยน",
            Theme.Small, Theme.Muted, wrap: true));
        _adv = new DataGridView
        {
            Height = 210, Anchor = AnchorStyles.Left | AnchorStyles.Right,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2, Margin = new Padding(0, 4, 0, 4),
        };
        Theme.StyleGrid(_adv);
        _adv.ReadOnly = false;
        _adv.Columns.Add(new DataGridViewTextBoxColumn { Name = "k", HeaderText = "Key", FillWeight = 40, SortMode = DataGridViewColumnSortMode.NotSortable });
        _adv.Columns.Add(new DataGridViewTextBoxColumn { Name = "v", HeaderText = "Value", FillWeight = 60, SortMode = DataGridViewColumnSortMode.NotSortable });
        _adv.Columns[0].DefaultCellStyle.Font = Theme.Mono;
        _adv.Columns[1].DefaultCellStyle.Font = Theme.Mono;
        _adv.CellValueChanged += (_, _) => Changed();
        _adv.RowsRemoved += (_, _) => Changed();
        _adv.EditingControlShowing += (_, e) => { e.Control.BackColor = Theme.Input; e.Control.ForeColor = Theme.Fg; };
        _adv.CellBeginEdit += (_, e) =>
        {
            // A masked secret: start from empty so the owner types the new value.
            if (e.ColumnIndex == 1 && _adv.Rows[e.RowIndex].Tag is AdvTag t && EnvDocument.IsSecret(t.Key)
                && _adv.Rows[e.RowIndex].Cells[1].Value as string == SecretMask)
                _adv.Rows[e.RowIndex].Cells[1].Value = "";
        };
        _adv.CellEndEdit += (_, e) =>
        {
            if (e.ColumnIndex == 1 && _adv.Rows[e.RowIndex].Tag is AdvTag t && EnvDocument.IsSecret(t.Key)
                && string.IsNullOrEmpty(_adv.Rows[e.RowIndex].Cells[1].Value as string))
                _adv.Rows[e.RowIndex].Cells[1].Value = SecretMask;   // left empty = keep the old secret
        };
        adv.Add(_adv);
        _btnAddRow = Theme.Button("เพิ่มแถว", Theme.BtnGray, (_, _) =>
        {
            int i = _adv.Rows.Add("", "");
            _adv.CurrentCell = _adv.Rows[i].Cells[0];
            _adv.BeginEdit(true);
        });
        _btnDelRow = Theme.Button("ลบแถวที่เลือก", Theme.BtnGray, (_, _) =>
        {
            foreach (DataGridViewRow r in _adv.SelectedRows) _adv.Rows.Remove(r);
        });
        adv.Add(Theme.Row(_btnAddRow, _btnDelRow));
        Stack(adv);

        // ── auto-start ──
        var auto = new Card("ทางลัด & เปิดอัตโนมัติ");
        _chkShortcut = auto.Add(Theme.Check("ทางลัด “BrainX Server Manager” ใน Start Menu (ทุกผู้ใช้)"));
        _chkTask = auto.Add(Theme.Check("เปิดอัตโนมัติเมื่อ logon — Scheduled Task สิทธิ์สูงสุด (Run key เปิดแอปที่ต้องใช้ Admin ไม่ได้)"));
        _autoStatus = auto.Add(Theme.Text("", Theme.Small, Theme.Muted, wrap: true));
        _btnApplyAuto = Theme.Button("ใช้การตั้งค่านี้", Theme.BtnBlue, async (_, _) => await ApplyAutoStartAsync());
        auto.Add(Theme.Row(_btnApplyAuto));
        Stack(auto);

        // ── paths ──
        var paths = new Card("ตำแหน่งไฟล์");
        var kv = paths.Add(new KvTable());
        kv.Add("โฟลเดอร์หลัก", b.Paths.Root);
        kv.Add("Service exe", b.Paths.ServiceExe ?? "—");
        kv.Add("Token", b.Paths.TokenFile);
        kv.Add("install-log", b.Paths.InstallLog);
        _pLogDir = kv.Add("Log ของ node", "…");
        kv.Add("สำรอง Environment", b.Paths.BackupDir);
        kv.Add("Server Manager", $"{AppInfo.ShortVersion} · {Environment.ProcessPath}");
        kv.Add("ต้นฉบับ (origin)", ctx.Args.Origin ?? "— (ไม่ได้รันจากสำเนา)");
        kv.Add("ทางลัด/Task ชี้ไปที่", ctx.TargetExe);
        paths.Add(Theme.Row(
            Theme.Button("เปิดโฟลเดอร์สำรอง", Theme.BtnGray, (_, _) => Shell.Open(b.Paths.BackupDir)),
            Theme.Button("เปิด manager.log", Theme.BtnGray, (_, _) => Shell.Reveal(ManagerLog.FilePath))));
        Stack(paths);

        Controls.Add(stack);
        UpdateButtons();
    }

    private void AddField(TableLayoutPanel t, EnvField f)
    {
        var key = Theme.Text(f.Label, Theme.Bold, Theme.Fg);
        key.Margin = new Padding(0, 6, 16, 0);
        Control input;
        FlatButton? extra = null;
        switch (f.Kind)
        {
            case EnvFieldKind.Path:
            case EnvFieldKind.Text:
                var tb = Theme.TextInput();
                tb.Anchor = AnchorStyles.Left | AnchorStyles.Right;
                tb.TextChanged += (_, _) => Changed();
                input = tb;
                if (f.Kind == EnvFieldKind.Path) extra = Theme.Button("เลือก…", Theme.BtnGray, (_, _) => Browse(tb));
                break;
            case EnvFieldKind.Bool:
                var cb = Theme.Check("เปิดใช้งาน");
                cb.CheckedChanged += (_, _) => Changed();
                input = cb;
                break;
            case EnvFieldKind.Int:
                var n = new NumericUpDown { Minimum = f.Min, Maximum = f.Max, Width = 130, ThousandsSeparator = true, Margin = new Padding(0, 2, 6, 2) };
                Theme.StyleInput(n);
                n.ValueChanged += (_, _) => Changed();
                input = n;
                break;
            default:
                var lbl = Theme.Text("…", Theme.Body, Theme.Fg);
                lbl.Margin = new Padding(0, 6, 0, 0);
                input = lbl;
                break;
        }
        var note = Theme.Text(f.Hint, Theme.Small, Theme.Muted, wrap: true);
        note.Margin = new Padding(0, 0, 0, 8);

        var editor = new FieldEditor { Field = f, Input = input, Note = note, Extra = extra };
        if (f.Kind == EnvFieldKind.ReadOnly)
        {
            editor.Extra = Theme.Button("ตั้งเป็น false", Theme.BtnAmber, (_, _) => { editor.Pending = "false"; Changed(); });
            editor.Extra.Visible = false;
        }

        int row = t.RowCount;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(key, 0, row);
        t.Controls.Add(input, 1, row);
        if (editor.Extra != null) t.Controls.Add(editor.Extra, 2, row);
        t.Controls.Add(note, 1, row + 1);
        t.SetColumnSpan(note, 2);
        t.RowCount = row + 2;
        _fields[f.Key] = editor;
    }

    // ───────────────────────── page lifecycle ─────────────────────────

    public async Task OnShownAsync()
    {
        if (!_loaded && !_loading) await LoadAsync();
        else await LoadAutoStartAsync();
    }

    public void OnTick(long nowMs) { }
    public bool CanLeave() => true;   // edits stay on the page; the shell warns on exit
    public Task PrepareScreenshotAsync() => LoadAsync();

    // ───────────────────────── load ─────────────────────────

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            var env = await _ctx.Backend.ReadEnvironmentAsync(_ctx.Life);
            if (!_ctx.Alive) return;
            if (!env.Ok)
            {
                _envError.Text = env.Error ?? "อ่าน Registry ไม่สำเร็จ";
                _loaded = false;
                return;
            }
            _original = env.Lines.ToList();
            _wrongKind = env.WrongValueKind;
            _envError.Text = _wrongKind
                ? "⚠ ค่า Environment ใน Registry เป็น REG_SZ แต่ Windows อ่านเฉพาะ REG_MULTI_SZ — กดบันทึกเพื่อแก้ชนิดให้ถูก"
                : "";
            foreach (var f in _fields.Values) Fill(f, EnvDocument.Get(_original, f.Field.Key));
            FillAdvanced();
            _loaded = true;
        }
        finally
        {
            _loading = false;
            if (_ctx.Alive) { UpdateButtons(); UpdateWarnings(); }
        }
        if (!_ctx.Alive) return;
        _pLogDir.Text = await _ctx.Backend.ResolveLogDirAsync(_ctx.Life);
        await LoadAutoStartAsync();
    }

    private void Fill(FieldEditor e, string? raw)
    {
        var f = e.Field;
        var v = raw?.Trim();
        e.Pending = null;
        e.LoadNote = "";
        switch (e.Input)
        {
            case TextBox tb:
                tb.Text = v ?? "";
                e.OriginalEffective = v ?? "";
                break;
            case CheckBox cb:
                bool parsed = bool.TryParse(v, out var bv);
                bool eff = parsed ? bv : bool.Parse(f.Default ?? "false");
                cb.Checked = eff;
                e.OriginalEffective = eff ? "true" : "false";
                if (v != null && !parsed) e.LoadNote = $"ค่าเดิม “{v}” ไม่ใช่ true/false — node ใช้ค่าเริ่มต้น ({f.Default})";
                break;
            case NumericUpDown n:
                bool ok = long.TryParse(v, NumberStyles.Integer, Inv, out var lv) && lv >= f.Min && lv <= f.Max;
                long val = ok ? lv : long.Parse(f.Default ?? "1", Inv);
                n.Value = val;
                e.OriginalEffective = val.ToString(Inv);
                if (v != null && !ok) e.LoadNote = $"ค่าเดิม “{v}” ใช้ไม่ได้ (ช่วง {f.Min}–{f.Max}) — แสดงค่าเริ่มต้น; ถ้าไม่แก้ บรรทัดเดิมจะคงไว้";
                break;
            case Label lbl:
                var effective = (v ?? f.Default ?? "").ToLowerInvariant();
                lbl.Text = v == null ? $"{effective}  (ไม่ได้ตั้ง — ค่าเริ่มต้นของ node)" : effective;
                e.OriginalEffective = effective;
                break;
        }
        ResetNote(e);
    }

    private void FillAdvanced()
    {
        _adv.Rows.Clear();
        for (int i = 0; i < _original.Count; i++)
        {
            if (EnvDocument.Split(_original[i]) is not { } kv || EnvDocument.IsManaged(kv.Key)) continue;
            int r = _adv.Rows.Add(kv.Key, EnvDocument.IsSecret(kv.Key) && kv.Value.Length > 0 ? SecretMask : kv.Value);
            _adv.Rows[r].Tag = new AdvTag(i, kv.Key, kv.Value);
        }
        _adv.ClearSelection();
    }

    private async Task LoadAutoStartAsync()
    {
        var s = await _ctx.Backend.GetAutoStartAsync(_ctx.TargetExe, _ctx.Life);
        if (!_ctx.Alive || _autoBusy) return;
        _chkShortcut.Checked = s.ShortcutExists;
        _chkTask.Checked = s.TaskExists;
        var parts = new List<string>
        {
            s.ShortcutExists ? (s.ShortcutCurrent ? "ทางลัด: มี ✓" : $"ทางลัด: มี แต่ชี้ไปที่ {s.ShortcutTarget ?? "?"}") : "ทางลัด: ไม่มี",
            s.TaskExists ? (s.TaskCurrent ? "Task: มี ✓" : $"Task: มี แต่ชี้ไปที่ {s.TaskCommand ?? "?"}") : "Task: ไม่มี",
        };
        if (s.Error != null) parts.Add(s.Error);
        _autoStatus.Text = string.Join(" · ", parts) + ((s.ShortcutExists && !s.ShortcutCurrent) || (s.TaskExists && !s.TaskCurrent) ? " — กด “ใช้การตั้งค่านี้” เพื่อชี้ไปที่ตัวปัจจุบัน" : "");
        _autoStatus.ForeColor = (s.ShortcutExists && !s.ShortcutCurrent) || (s.TaskExists && !s.TaskCurrent) ? Theme.Warn : Theme.Muted;
    }

    // ───────────────────────── editing state ─────────────────────────

    private void Changed()
    {
        if (_loading) return;
        UpdateButtons();
        UpdateWarnings();
    }

    private static string Current(FieldEditor e) => e.Input switch
    {
        TextBox tb => tb.Text.Trim(),
        CheckBox cb => cb.Checked ? "true" : "false",
        NumericUpDown n => ((long)n.Value).ToString(Inv),
        _ => e.Pending ?? e.OriginalEffective,
    };

    private List<EnvDocument.AdvancedRow> AdvancedRows()
    {
        var list = new List<EnvDocument.AdvancedRow>();
        foreach (DataGridViewRow r in _adv.Rows)
        {
            var key = (r.Cells[0].Value as string ?? "").Trim();
            var val = r.Cells[1].Value as string ?? "";
            var tag = r.Tag as AdvTag;
            if (tag != null && EnvDocument.IsSecret(tag.Key) && val == SecretMask) val = tag.Value;
            if (tag == null && key.Length == 0 && val.Length == 0) continue;   // an untouched new row
            list.Add(new EnvDocument.AdvancedRow(tag?.Index, key, val));
        }
        return list;
    }

    private List<string> Compose()
    {
        var changed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _fields.Values)
        {
            var cur = Current(e);
            if (string.Equals(cur, e.OriginalEffective, StringComparison.Ordinal)) continue;
            changed[e.Field.Key] = cur.Length == 0 ? null : cur;
        }
        return EnvDocument.Compose(_original, changed, AdvancedRows());
    }

    private bool IsDirty() => _wrongKind || !Compose().SequenceEqual(_original, StringComparer.Ordinal);

    private void UpdateButtons()
    {
        bool admin = _ctx.Backend.IsElevated;
        bool dirty = _loaded && IsDirty();
        _btnSave.Enabled = admin && _loaded && dirty && !_saving;
        _btnRevert.Enabled = _loaded && dirty && !_saving;
        _btnReload.Enabled = !_saving && !_loading;
        _dirty.Text = _saving ? "กำลังบันทึก…"
            : !admin ? "อ่านอย่างเดียว — เปิดแบบ Administrator เพื่อแก้"
            : dirty ? "● มีการแก้ไขที่ยังไม่บันทึก"
            : _loaded ? "ตรงกับ Registry" : "";
        _dirty.ForeColor = dirty || !admin ? Theme.Warn : Theme.Muted;

        foreach (var e in _fields.Values)
        {
            switch (e.Input)
            {
                case TextBox tb: tb.ReadOnly = !admin || _saving; break;
                case CheckBox cb: cb.Enabled = admin && !_saving; break;
                case NumericUpDown n: n.Enabled = admin && !_saving; break;
            }
            if (e.Extra != null && e.Field.Kind == EnvFieldKind.Path) e.Extra.Enabled = admin && !_saving;
        }
        bool advReadOnly = !admin || _saving;
        if (_adv.ReadOnly != advReadOnly) _adv.ReadOnly = advReadOnly;   // re-setting it would end a cell edit
        _btnAddRow.Enabled = _btnDelRow.Enabled = admin && _loaded && !_saving;
        _btnApplyAuto.Enabled = admin && !_autoBusy;
        _chkShortcut.Enabled = _chkTask.Enabled = admin && !_autoBusy;
    }

    private void UpdateWarnings()
    {
        if (_fields.TryGetValue(RequireAuthKey, out var auth))
        {
            _authWarn.Text = Current(auth) == "false"
                ? "⚠ RequireAuth ปิดอยู่: ใครก็ตามที่เข้าถึง https://serverbrain.xman4289.com เรียก /api ได้โดยไม่ต้องมี Token — รวมถึงจุดที่เขียนข้อมูล ห้ามปิดบนเซิร์ฟเวอร์จริง"
                : "";
            _authWarn.Visible = _authWarn.Text.Length > 0;
        }
        if (_fields.TryGetValue(EmbeddedKey, out var emb))
        {
            bool bad = Current(emb) != "false";
            if (emb.Extra != null) emb.Extra.Visible = bad && _ctx.Backend.IsElevated;
            if (bad) SetNote(emb, "⚠ EmbeddedMode ต้องเป็น false บนเซิร์ฟเวอร์ — ค่านี้ทำให้ node เปิดโล่ง (ไม่ตรวจ Token, CORS ทุก origin)", Theme.Bad);
            else if (emb.Pending != null) SetNote(emb, "จะตั้งเป็น false เมื่อกดบันทึก", Theme.Warn);
            else ResetNote(emb);
        }
    }

    private static void SetNote(FieldEditor e, string text, Color color)
    {
        e.Note.Text = text;
        e.Note.ForeColor = color;
    }

    private static void ResetNote(FieldEditor e)
    {
        if (e.LoadNote.Length > 0) SetNote(e, e.LoadNote, Theme.Warn);
        else SetNote(e, e.Field.Hint, Theme.Muted);
    }

    // ───────────────────────── validate + save ─────────────────────────

    private List<string> ValidateFields()
    {
        var errors = new List<string>();
        foreach (var e in _fields.Values) ResetNote(e);
        UpdateWarnings();
        void Err(string key, string msg)
        {
            var e = _fields[key];
            SetNote(e, msg, Theme.Bad);
            errors.Add($"{e.Field.Label}: {msg}");
        }

        var vault = Current(_fields["BrainX__VaultPath"]);
        if (vault.Length == 0) Err("BrainX__VaultPath", "ต้องระบุโฟลเดอร์ vault");
        else if (!Path.IsPathFullyQualified(vault)) Err("BrainX__VaultPath", @"ต้องเป็นพาธเต็ม เช่น C:\brainx\vault");
        else if (!_ctx.Backend.DirectoryExists(vault)) Err("BrainX__VaultPath", "ไม่พบโฟลเดอร์นี้บนเครื่อง");

        foreach (var key in new[] { "BrainX__CloudRoot", "BrainX__LogDir" })
        {
            var v = Current(_fields[key]);
            if (v.Length == 0) continue;
            if (!Path.IsPathFullyQualified(v)) Err(key, "ต้องเป็นพาธเต็ม (มีไดรฟ์ เช่น C:\\...)");
            else if (!_ctx.Backend.DirectoryExists(v) && !(Path.GetDirectoryName(v) is { } parent && _ctx.Backend.DirectoryExists(parent)))
                Err(key, "ไม่พบทั้งโฟลเดอร์นี้และโฟลเดอร์แม่ของมัน");
        }

        foreach (var o in Current(_fields["BrainX__AllowedOrigins"]).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(o, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)
                || u.AbsolutePath != "/" || u.Query.Length > 0 || o.EndsWith('/'))
            {
                Err("BrainX__AllowedOrigins", $"“{o}” ไม่ใช่ origin — ต้องเป็นแบบ https://serverbrain.xman4289.com (ไม่มี / หรือ path ท้าย)");
                break;
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in AdvancedRows())
        {
            if (EnvDocument.ValidateKey(r.Key) is { } ke) { errors.Add("Advanced: " + ke); continue; }
            if (EnvDocument.IsManaged(r.Key)) { errors.Add($"Advanced: {r.Key} แก้ที่ช่องด้านบน (หรือแท็บ Token) — ลบแถวนี้ออก"); continue; }
            if (!seen.Add(EnvDocument.Normalize(r.Key))) errors.Add($"Advanced: {r.Key} มีซ้ำ");
            if (EnvDocument.ValidateValue(r.Key, r.Value) is { } ve) errors.Add("Advanced: " + ve);
        }
        return errors;
    }

    private async Task SaveAsync()
    {
        if (_saving || !_loaded) return;
        var b = _ctx.Backend;
        if (!b.IsElevated) { _ctx.ShowError("ต้องใช้สิทธิ์ Administrator", ErrorText.NeedAdmin); return; }
        if (_adv.IsCurrentCellInEditMode) _adv.EndEdit();

        var errors = ValidateFields();
        if (errors.Count > 0)
        {
            _ctx.ShowError("ยังบันทึกไม่ได้ — มีค่าที่ไม่ถูกต้อง", string.Join("\n", errors.Select(e => "• " + e)));
            return;
        }
        var lines = Compose();
        if (!_wrongKind && lines.SequenceEqual(_original, StringComparer.Ordinal)) { _ctx.Notify("ไม่มีการเปลี่ยนแปลง", false); return; }

        var diff = EnvDocument.Diff(_original, lines);
        if (_wrongKind) diff.Add("~ ชนิดค่า REG_SZ → REG_MULTI_SZ");
        var auth = _fields[RequireAuthKey];
        bool authOff = Current(auth) == "false" && auth.OriginalEffective != "false";
        if (!ConfirmDialog.Ask(_ctx.Owner, "บันทึก Service Environment?",
                "จะเขียนการเปลี่ยนแปลงนี้ลง Registry (สำรองค่าเดิมไว้ใน manager-backups ก่อน):", "บันทึก", danger: authOff,
                detail: string.Join("\n", diff),
                warning: authOff ? "คุณกำลังปิด RequireAuth — ใครก็ตามที่เข้าถึง https://serverbrain.xman4289.com จะเรียก /api ได้โดยไม่ต้องมี Token รวมถึงจุดที่เขียนข้อมูล" : null,
                typeToConfirm: authOff ? "RequireAuth=false" : null))
            return;

        _saving = true;
        UpdateButtons();
        EnvWriteResult r;
        try { r = await b.WriteEnvironmentAsync(lines, _original, _ctx.Life); }
        finally { _saving = false; }
        if (!_ctx.Alive) return;

        switch (r.Status)
        {
            case EnvWriteStatus.Written:
                _ctx.Notify($"บันทึก Service Environment แล้ว · สำรองค่าเดิมไว้ที่ {r.BackupPath}", false);
                await LoadAsync();
                if (_ctx.Alive && ConfirmDialog.Ask(_ctx.Owner, $"รีสตาร์ท {b.NodeServiceName} ตอนนี้?", "ค่าใหม่จะมีผลหลัง Service รีสตาร์ท", "Restart ตอนนี้", danger: false,
                        detail: "• ลูกค้าที่เชื่อมต่ออยู่จะหลุดประมาณ 5–10 วินาที\n• เลือก “ยกเลิก” แล้วไปรีสตาร์ทเองภายหลังที่หน้า ภาพรวม ได้"))
                    await _ctx.Services.RunAsync(b.NodeServiceName, ServiceAction.Restart, skipConfirm: true);
                break;
            case EnvWriteStatus.Conflict:
                if (ConfirmDialog.Ask(_ctx.Owner, "ค่าใน Registry เปลี่ยนไปแล้ว", r.Message, "โหลดใหม่ (ทิ้งการแก้ไขในหน้านี้)", danger: true))
                    await LoadAsync();
                break;
            default:
                _ctx.ShowError("บันทึกไม่สำเร็จ — การแก้ไขยังอยู่ในหน้านี้", r.Message);
                break;
        }
        if (_ctx.Alive) UpdateButtons();
    }

    private async Task RevertAsync()
    {
        if (!IsDirty()) return;
        if (!ConfirmDialog.Ask(_ctx.Owner, "ยกเลิกการแก้ไข?", "ทุกค่าที่แก้ในหน้านี้จะกลับไปเป็นค่าใน Registry", "ยกเลิกการแก้ไข", danger: true)) return;
        await LoadAsync();
    }

    private async Task ReloadClickedAsync()
    {
        if (_loaded && IsDirty() &&
            !ConfirmDialog.Ask(_ctx.Owner, "โหลดใหม่จาก Registry?", "การแก้ไขที่ยังไม่บันทึกในหน้านี้จะหาย", "โหลดใหม่", danger: true))
            return;
        await LoadAsync();
    }

    private void Browse(TextBox tb)
    {
        using var d = new FolderBrowserDialog { ShowNewFolderButton = true, UseDescriptionForTitle = true, Description = "เลือกโฟลเดอร์" };
        if (Directory.Exists(tb.Text)) d.InitialDirectory = tb.Text;
        if (d.ShowDialog(_ctx.Owner) == DialogResult.OK) tb.Text = d.SelectedPath;
    }

    private async Task ApplyAutoStartAsync()
    {
        if (_autoBusy) return;
        _autoBusy = true;
        UpdateButtons();
        _autoStatus.Text = "กำลังบันทึก…";
        OpResult r;
        try { r = await _ctx.Backend.SetAutoStartAsync(_ctx.TargetExe, _chkShortcut.Checked, _chkTask.Checked, _ctx.Life); }
        finally { _autoBusy = false; }
        if (!_ctx.Alive) return;
        if (r.Ok) _ctx.Notify(r.Message, false);
        else _ctx.ShowError("ตั้งค่าเปิดอัตโนมัติไม่สำเร็จ", r.Message);
        UpdateButtons();
        await LoadAutoStartAsync();
    }
}
