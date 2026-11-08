// SetupWizardWindow.Welcome.cs - page 1 (BxWelcome): where the brain lives,
// which agents to connect, and — when CluadeX is not on this PC — the CluadeX
// pitch with "ติดตั้ง CluadeX ด้วย" (contract §2.7, §2.8).

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BrainX.Core.Services.Setup;
using Newtonsoft.Json.Linq;
using static BrainX.Client.Setup.SetupTheme;

namespace BrainX.Client.Setup;

internal sealed partial class SetupWizardWindow
{
    private string _vault = "";
    private string? _vaultBefore;
    private bool _vaultBeforeRead;
    private bool _linkClaude = true;
    private bool _linkCluadeX = true;
    private bool _installCluadeX;
    private bool? _cluadeXPresent;
    private TextBlock? _vaultText;
    private TextBlock? _vaultHint;

    private const string VaultHintDefault = "มี vault Obsidian อยู่แล้ว? เลือกโฟลเดอร์นั้นได้เลย โน้ตเดิมใช้ต่อได้ทันที";

    private FrameworkElement BuildWelcome()
    {
        if (!_vaultBeforeRead && !_render)
        {
            _vaultBeforeRead = true;
            _vaultBefore = VaultLocations.ReadPointer();
        }
        if (_vault.Length == 0) _vault = VaultLocations.Current();
        _cluadeXPresent ??= CluadeXSetup.IsPresent();
        var present = _cluadeXPresent == true;

        var banner = new Border
        {
            Width = 400, Height = 158, CornerRadius = new CornerRadius(16), HorizontalAlignment = HorizontalAlignment.Left,
            BorderBrush = Brush(0.35, 0x7D, 0xD3, 0xFC), BorderThickness = new Thickness(1),
            Background = new ImageBrush(Pack("Assets/LogoBanner.png")) { Stretch = Stretch.UniformToFill },
            Effect = Glow(Sky, 26, 0.35),
        };
        var h1 = T("สมองที่สองของ AI ทุกตัวในเครื่องคุณ", 26, Text, FontWeights.SemiBold, 35);
        var p = T("BrainX เก็บความรู้ บทเรียน และงานค้างเป็นโน้ต ให้ AI ค้นและจำได้ข้ามทุกแชต ทุกโปรแกรม", 14.5, Muted, lineHeight: 24.5);

        // where the brain lives
        var folder = Icon(IconFolder, SkyLight, 18, 1.7);
        var label = T("เก็บสมองที่", 13, Muted, wrap: false);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(10, 0, 10, 0);
        _vaultText = T(DisplayPath(_vault), 13, Text, wrap: false);
        _vaultText.FontFamily = Mono;
        _vaultText.VerticalAlignment = VerticalAlignment.Center;
        _vaultText.ToolTip = _vault;
        var change = SmallButton("เปลี่ยน");
        change.Margin = new Thickness(10, 0, 0, 0);
        change.Click += (_, _) => OpenVaultMenu(change);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(folder);
        Grid.SetColumn(label, 1); row.Children.Add(label);
        Grid.SetColumn(_vaultText, 2); row.Children.Add(_vaultText);
        Grid.SetColumn(change, 3); row.Children.Add(change);
        _vaultHint = T(VaultHintDefault, 11.5, Faint, lineHeight: 17);
        _vaultHint.Margin = new Thickness(28, -6, 0, 0);

        var claude = Check("เชื่อมกับ Claude Code และ Claude Desktop ให้อัตโนมัติ", _linkClaude);
        claude.Checked += (_, _) => _linkClaude = true;
        claude.Unchecked += (_, _) => _linkClaude = false;
        var panelBody = Stack(12, row, _vaultHint, Separator(), claude);
        if (present)
        {
            var cxLabel = T("เชื่อมกับ CluadeX", 14, Text, wrap: false);
            cxLabel.VerticalAlignment = VerticalAlignment.Center;
            var cx = Check(Row(10, cxLabel, TextPill("พบในเครื่องนี้", GreenLight, Green, 0.14)), _linkCluadeX);
            cx.Checked += (_, _) => _linkCluadeX = true;
            cx.Unchecked += (_, _) => _linkCluadeX = false;
            AddTo(panelBody, cx, 12);
        }
        var panel = Panel(panelBody);

        var start = PrimaryButton("เริ่มเลย", 52, 16);
        start.Click += (_, _) => WelcomeStart();
        start.IsDefault = true;
        var cancel = GhostButton("ยกเลิก", 52, 15);
        cancel.Click += (_, _) => RequestClose();

        var col = Stack(16, banner, Stack(8, h1, p), panel, Row(12, start, cancel));
        col.Width = 480;
        col.HorizontalAlignment = HorizontalAlignment.Left;
        col.VerticalAlignment = VerticalAlignment.Top;
        col.Margin = new Thickness(56, 60, 0, 0);

        var page = new Grid();
        page.Children.Add(col);
        // The girl steps aside a little for the CluadeX card.
        _girl.Height = present ? 590 : 560;
        _girl.Margin = present ? new Thickness(0, 0, 70, -10) : new Thickness(0, 0, -16, -10);
        if (!present) page.Children.Add(BuildPitch());
        if (!_render) _ = RefreshVaultHintAsync();
        return page;
    }

    private FrameworkElement BuildPitch()
    {
        var logo = new Image { Source = Pack("Assets/Installer/CluadeXLogo.png", 96), Width = 44, Height = 44 };
        var tag = new Border
        {
            CornerRadius = new CornerRadius(999), Background = Primary(), Padding = new Thickness(10, 2, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Child = T("แนะนำ", 11.5, Colors.White, FontWeights.SemiBold, wrap: false),
            Effect = Glow(Sky, 12, 0.5),
        };
        var head = new Grid();
        head.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = logo });
        head.Children.Add(tag);
        var title = T("CluadeX — IDE สาย vibe code ที่อยู่บนเครื่องคุณ", 15.5, Text, FontWeights.SemiBold, 22);
        string[] bullets =
        [
            "บอกเป็นภาษาไทยว่าอยากได้อะไร แล้ว AI วางแผน เขียน แก้ และรันโค้ดทั้งโปรเจกต์ให้",
            "ต่อ Claude, Gemini, DeepSeek หรือโมเดลที่รันบนการ์ดจอของคุณเอง สลับได้ในที่เดียว",
            "ต่อกับ BrainX แล้ว AI จำโปรเจกต์ บทเรียนจากบั๊ก และงานค้างได้ข้ามทุกแชต",
            "มีสาวๆ ผู้ช่วยคอยรายงานว่า AI กำลังทำอะไร ไม่ต้องเดา",
        ];
        var list = new StackPanel();
        foreach (var b in bullets)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var tick = Icon(IconCheck, SkyLight, 12, 2.6);
            tick.VerticalAlignment = VerticalAlignment.Top;
            tick.Margin = new Thickness(0, 4, 0, 0);
            g.Children.Add(tick);
            var t = T(b, 12.5, Soft, lineHeight: 19);
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            AddTo(list, g, 8);
        }
        var sw = new CheckBox
        {
            Style = (Style)_styles["SxSwitch"],
            IsChecked = _installCluadeX,
            Content = T("ติดตั้ง CluadeX ด้วย", 14.5, Text, FontWeights.SemiBold, wrap: false),
        };
        var size = T(CluadeXSetup.ApproxSize, 11.5, Faint, wrap: false);
        size.Margin = new Thickness(0, -6, 0, 0);

        var bg = new LinearGradientBrush(Argb(0.90, 0x0C, 0x1A, 0x40), Argb(0.90, 0x22, 0x12, 0x4E), new Point(0, 0), new Point(1, 1));
        bg.Freeze();
        var card = new Border
        {
            Width = 272,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(560, 84, 0, 0),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18, 16, 18, 16),
            Background = bg,
            BorderThickness = new Thickness(1.2),
            Child = Stack(12, head, title, list, Separator(), sw, size),
        };
        void Paint()
        {
            var on = sw.IsChecked == true;
            card.BorderBrush = on ? Brush(0.85, 0x38, 0xBD, 0xF8) : Brush(0.30, 0x7D, 0xD3, 0xFC);
            card.Effect = on ? Glow(Sky, 34, 0.55) : Glow(Sky, 18, 0.18);
        }
        sw.Checked += (_, _) => { _installCluadeX = true; Paint(); };
        sw.Unchecked += (_, _) => { _installCluadeX = false; Paint(); };
        Paint();
        return card;
    }

    // ── choosing the vault ───────────────────────────────────────────

    private void OpenVaultMenu(Button anchor)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            Background = Brush(Argb(0.98, 0x0B, 0x13, 0x30)),
            Foreground = Brush(Text),
            BorderBrush = Brush(0.3, 0x7D, 0xD3, 0xFC),
            FontFamily = Font,
            FontSize = 13,
        };
        MenuItem Item(string header, string? path, Action? act = null)
        {
            var mi = new MenuItem { Header = header, Foreground = Brush(Text), ToolTip = path };
            mi.Click += (_, _) => { if (act is not null) act(); else if (path is not null) SetVault(path); };
            return mi;
        }
        foreach (var v in VaultLocations.ObsidianVaults().Take(6))
        {
            if (string.Equals(v, _vault, StringComparison.OrdinalIgnoreCase)) continue;
            menu.Items.Add(Item("vault Obsidian: " + DisplayPath(v), v));
        }
        if (!string.Equals(VaultLocations.DefaultVault, _vault, StringComparison.OrdinalIgnoreCase))
            menu.Items.Add(Item("ค่าเริ่มต้น: " + DisplayPath(VaultLocations.DefaultVault), VaultLocations.DefaultVault));
        if (_vaultBefore is { } before && !string.Equals(before, _vault, StringComparison.OrdinalIgnoreCase))
            menu.Items.Add(Item("ที่เดิม: " + DisplayPath(before), before));
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        menu.Items.Add(Item("เลือกโฟลเดอร์อื่น…", null, PickFolder));
        menu.IsOpen = true;
    }

    private void PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "เลือกโฟลเดอร์เก็บสมองของ BrainX" };
        try
        {
            var parent = Path.GetDirectoryName(_vault);
            if (parent is not null && Directory.Exists(parent)) dlg.InitialDirectory = parent;
        }
        catch { /* the dialog opens at its default */ }
        if (dlg.ShowDialog(this) == true) SetVault(dlg.FolderName);
    }

    private void SetVault(string path)
    {
        try { path = Path.GetFullPath(path); }
        catch { ShowVaultHint("ที่อยู่โฟลเดอร์นี้ใช้ไม่ได้ ลองเลือกใหม่", RedLight); return; }
        _vault = path;
        if (_vaultText is not null) { _vaultText.Text = DisplayPath(path); _vaultText.ToolTip = path; }
        _ = RefreshVaultHintAsync();
    }

    private void ShowVaultHint(string text, Color color)
    {
        if (_vaultHint is null) return;
        _vaultHint.Text = text;
        _vaultHint.Foreground = Brush(color);
    }

    /// <summary>Count the notes already there, and warn a returning user who moves the brain.</summary>
    private async Task RefreshVaultHintAsync()
    {
        var path = _vault;
        int notes;
        try { notes = Directory.Exists(path) ? await Task.Run(() => VaultLocations.CountNotes(path, 100_000)) : 0; }
        catch { notes = 0; }
        if (path != _vault || _page != PageWelcome) return;   // chose again meanwhile
        if (_vaultBefore is { } before && !string.Equals(before, path, StringComparison.OrdinalIgnoreCase))
            ShowVaultHint($"เดิมใช้ {DisplayPath(before)} ถ้าเปลี่ยน BrainX จะใช้โฟลเดอร์ใหม่ โน้ตที่เดิมยังอยู่ครบ ไม่ถูกย้ายหรือลบ", Amber);
        else if (notes > 0)
            ShowVaultHint($"พบโน้ต {notes:N0} ไฟล์ในโฟลเดอร์นี้ ใช้ต่อได้ทันที", SkyLight);
        else
            ShowVaultHint(VaultHintDefault, Faint);
    }

    private void WelcomeStart()
    {
        if (_busy) return;
        if (VaultLocations.Prepare(_vault) is { } err)
        {
            ShowVaultHint(err, RedLight);
            return;
        }
        SaveAgentChoices(_linkClaude, _cluadeXPresent != true || _linkCluadeX);
        ShowPage(PageLicense);
    }

    /// <summary>
    /// The two boxes become machine settings MainWindow reads at start, so a
    /// box left unticked is not undone by the next launch's auto-connect.
    /// The file is merged, never replaced; an unreadable one is left alone.
    /// </summary>
    internal static void SaveAgentChoices(bool claude, bool cluadeX, string? path = null)
    {
        path ??= Path.Combine(VaultLocations.DataRoot, "machine-settings.json");
        try
        {
            JObject o;
            if (File.Exists(path))
            {
                try { o = JObject.Parse(File.ReadAllText(path)); }
                catch { return; }   // MainWindow backs up and repairs a broken file itself
            }
            else o = new JObject();
            o["McpAutoRegisterEnabled"] = claude;
            o["CluadeXAutoRegisterEnabled"] = cluadeX;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, o.ToString());
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"setup: machine settings: {ex.Message}"); }
    }
}
