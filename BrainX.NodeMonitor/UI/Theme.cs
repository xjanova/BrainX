using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace BrainX.ServerManager.UI;

/// <summary>
/// The dark look of the old Node Monitor, plus fonts that can draw Thai.
///
/// Font: GDI's own fallback for Thai under "Segoe UI" lands on Tahoma (Segoe UI's
/// SystemLink starts with TAHOMA.TTF), so a Segoe UI window would render Latin in
/// Segoe and Thai in Tahoma, at different heights. Leelawadee UI ships with every
/// Windows 10 / Server 2016+ build, carries both Latin and Thai, and matches
/// Segoe's proportions, so it is the UI font when present; Tahoma (Thai on every
/// Windows) and Segoe UI are the fallbacks.
///
/// Sizes: every form uses AutoScaleMode.Dpi from 96-DPI design units, fonts are
/// in points. <see cref="Scale"/> is 1 except in screenshot runs with
/// --emulate-scale, which lay out as if Windows ran at that scale.
/// </summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(10, 14, 26);
    public static readonly Color Sidebar = Color.FromArgb(13, 18, 33);
    public static readonly Color Card = Color.FromArgb(18, 24, 42);
    public static readonly Color CardBorder = Color.FromArgb(36, 46, 72);
    public static readonly Color Input = Color.FromArgb(6, 9, 17);
    public static readonly Color Fg = Color.FromArgb(220, 230, 245);
    public static readonly Color Muted = Color.FromArgb(125, 142, 172);
    public static readonly Color Accent = Color.FromArgb(0, 229, 255);
    public static readonly Color Ok = Color.FromArgb(74, 222, 128);
    public static readonly Color Warn = Color.FromArgb(255, 179, 0);
    public static readonly Color Bad = Color.FromArgb(248, 96, 96);
    public static readonly Color Unknown = Color.FromArgb(110, 120, 140);
    public static readonly Color Selected = Color.FromArgb(26, 36, 62);
    public static readonly Color GridLine = Color.FromArgb(30, 38, 60);

    public static readonly Color BtnGreen = Color.FromArgb(34, 120, 70);
    public static readonly Color BtnRed = Color.FromArgb(150, 50, 60);
    public static readonly Color BtnBlue = Color.FromArgb(40, 90, 140);
    public static readonly Color BtnGray = Color.FromArgb(50, 60, 86);
    public static readonly Color BtnAmber = Color.FromArgb(150, 104, 16);

    public static float Scale { get; private set; } = 1f;
    public static string Family { get; private set; } = "Segoe UI";
    public static bool HasGlyphFont { get; private set; }

    public static Font Body { get; private set; } = null!;
    public static Font Bold { get; private set; } = null!;
    public static Font Small { get; private set; } = null!;
    public static Font SmallBold { get; private set; } = null!;
    public static Font H2 { get; private set; } = null!;
    public static Font Title { get; private set; } = null!;
    public static Font Mono { get; private set; } = null!;
    public static Font Glyph { get; private set; } = null!;

    public static SizeF DesignDpi => new(96f / Scale, 96f / Scale);

    public static void Init(float emulateScale)
    {
        Scale = emulateScale;
        var installed = InstalledFamilies();
        Family = new[] { "Leelawadee UI", "Leelawadee", "Tahoma", "Segoe UI" }.FirstOrDefault(installed.Contains)
                 ?? SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Tahoma";
        HasGlyphFont = installed.Contains("Segoe MDL2 Assets");

        Body = F(9.5f);
        Bold = F(9.5f, FontStyle.Bold);
        Small = F(8.5f);
        SmallBold = F(8.5f, FontStyle.Bold);
        H2 = F(11f, FontStyle.Bold);
        Title = F(15f, FontStyle.Bold);
        Mono = new Font(installed.Contains("Consolas") ? "Consolas" : FontFamily.GenericMonospace.Name, 9f * Scale, FontStyle.Regular, GraphicsUnit.Point);
        Glyph = HasGlyphFont ? new Font("Segoe MDL2 Assets", 11f * Scale, FontStyle.Regular, GraphicsUnit.Point) : Body;
    }

    private static Font F(float pt, FontStyle st = FontStyle.Regular) => new(Family, pt * Scale, st, GraphicsUnit.Point);

    private static HashSet<string> InstalledFamilies()
    {
        try
        {
            using var fonts = new InstalledFontCollection();
            return new HashSet<string>(fonts.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    // ───────────────────────── control factories ─────────────────────────

    public static Label Text(string text, Font? font = null, Color? color = null, bool wrap = false)
    {
        var l = new Label
        {
            Text = text,
            Font = font ?? Body,
            ForeColor = color ?? Fg,
            BackColor = Color.Transparent,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2),
        };
        // In a TableLayoutPanel an AutoSize label anchored left+right wraps at the column width.
        if (wrap) l.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        return l;
    }

    public static FlatButton Button(string text, Color fill, EventHandler? onClick = null)
    {
        var b = new FlatButton { Text = text, Fill = fill };
        if (onClick != null) b.Click += onClick;
        return b;
    }

    public static TextBox TextInput(bool mono = false)
    {
        return new TextBox
        {
            BackColor = Input,
            ForeColor = Fg,
            BorderStyle = BorderStyle.FixedSingle,
            Font = mono ? Mono : Body,
            Margin = new Padding(0, 2, 6, 2),
        };
    }

    /// <summary>A drop-down list that draws its own items: the stock one keeps a white edit area in some renderings.</summary>
    public static ComboBox Combo(int width)
    {
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            FlatStyle = FlatStyle.Flat,
            BackColor = Input,
            ForeColor = Fg,
            Width = width,
            Margin = new Padding(0, 2, 8, 2),
        };
        cb.ItemHeight = TextRenderer.MeasureText("Ag", Body).Height + 4;
        cb.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool hot = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var bg = new SolidBrush(hot ? Selected : Input)) e.Graphics.FillRectangle(bg, e.Bounds);
            var r = e.Bounds;
            r.Inflate(-4, 0);
            TextRenderer.DrawText(e.Graphics, cb.Items[e.Index]?.ToString() ?? "", Body, r, Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        };
        return cb;
    }

    public static void StyleInput(Control c)
    {
        c.BackColor = Input;
        c.ForeColor = Fg;
        if (c is TextBox tb) tb.BorderStyle = BorderStyle.FixedSingle;
        if (c is NumericUpDown n) n.BorderStyle = BorderStyle.FixedSingle;
        if (c is ComboBox cb) cb.FlatStyle = FlatStyle.Flat;
    }

    /// <summary>
    /// System-drawn check box. FlatStyle.Flat paints the box white (SystemColors.Window)
    /// and the tick in ForeColor — a light tick on white, invisible in this theme.
    /// </summary>
    public static CheckBox Check(string text)
        => new() { Text = text, ForeColor = Fg, AutoSize = true, FlatStyle = FlatStyle.Standard, Margin = new Padding(0, 3, 12, 3), BackColor = Color.Transparent };

    /// <summary>
    /// A single row of buttons. Deliberately no wrapping: a wrapping FlowLayoutPanel
    /// inside an auto-sized TableLayoutPanel row gets measured as if it wrapped,
    /// which made every card as tall as three rows of buttons.
    /// </summary>
    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var f = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 6, 0, 0),
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
        };
        f.Controls.AddRange(controls);
        return f;
    }

    public static void StyleGrid(DataGridView g)
    {
        g.BackgroundColor = Card;
        g.BorderStyle = BorderStyle.None;
        g.GridColor = GridLine;
        g.EnableHeadersVisualStyles = false;
        g.RowHeadersVisible = false;
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.AllowUserToResizeRows = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = false;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Sidebar, ForeColor = Muted, Font = SmallBold,
            SelectionBackColor = Sidebar, SelectionForeColor = Muted,
            Padding = new Padding(6, 6, 6, 6), WrapMode = DataGridViewTriState.False,
        };
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Card, ForeColor = Fg, Font = Body,
            SelectionBackColor = Selected, SelectionForeColor = Color.White,
            Padding = new Padding(6, 0, 6, 0), WrapMode = DataGridViewTriState.False,
        };
        g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(20, 27, 47) };
        // Row height from the font, so it follows DPI and the Thai line height.
        g.RowTemplate.Height = (int)Math.Ceiling(Body.GetHeight() * 1.9f);
    }

    /// <summary>Pixels for a 96-DPI length, for sizes set after the forms have auto-scaled (custom painting).</summary>
    public static int Px(Control c, float designPx) => (int)Math.Round(designPx * c.DeviceDpi / 96f * Scale);

    // ───────────────────────── native niceties ─────────────────────────

    /// <summary>
    /// Dark title bar. Attribute 20 is the documented one (Windows 10 20H1+);
    /// 19 is what 1809/1903 — i.e. Server 2019 — understood. Both are no-ops
    /// elsewhere: the call just returns an error HRESULT.
    /// </summary>
    public static void DarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
        }
        catch { /* dwmapi missing on some Server Core images: cosmetic */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
