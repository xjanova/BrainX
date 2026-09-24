using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace BrainX.ServerManager.UI;

/// <summary>A flat button that also LOOKS disabled on a dark background (WinForms keeps the fill colour).</summary>
internal sealed class FlatButton : Button
{
    private bool _hover, _down;
    private Color _fill = Theme.BtnGray;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill
    {
        get => _fill;
        set { if (_fill != value) { _fill = value; Invalidate(); } }
    }

    public FlatButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Color.White;
        Font = Theme.Bold;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10, 5, 10, 5);
        Margin = new Padding(0, 0, 8, 6);
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor is { A: 255 } pb ? pb : Theme.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = !Enabled ? Color.FromArgb(34, 40, 58)
            : _down ? ControlPaint.Dark(Fill, 0.05f)
            : _hover ? ControlPaint.Light(Fill, 0.25f)
            : Fill;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Rounded(r, Math.Max(3, Height / 7f)))
        using (var brush = new SolidBrush(fill))
        using (var pen = new Pen(Enabled ? Color.FromArgb(70, 85, 110) : Color.FromArgb(44, 52, 74)))
        {
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle,
            Enabled ? ForeColor : Color.FromArgb(96, 108, 132),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
        {
            var fr = ClientRectangle;
            fr.Inflate(-3, -3);
            ControlPaint.DrawFocusRectangle(g, fr, Color.White, fill);
        }
    }

    internal static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

/// <summary>A titled panel: a one-column table with a 1 px border.</summary>
internal sealed class Card : TableLayoutPanel
{
    public Label? TitleLabel { get; }

    public Card(string? title = null, StatusDot? dot = null)
    {
        DoubleBuffered = true;
        BackColor = Theme.Card;
        Padding = new Padding(14, 12, 14, 10);
        Margin = new Padding(0, 0, 12, 12);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Dock = DockStyle.Fill;
        if (title != null)
        {
            TitleLabel = Theme.Text(title, Theme.H2, Theme.Fg);
            TitleLabel.Margin = new Padding(0, 0, 0, 8);
            if (dot != null)
            {
                var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, BackColor = Color.Transparent };
                dot.Margin = new Padding(0, 6, 8, 0);
                row.Controls.Add(dot);
                row.Controls.Add(TitleLabel);
                Add(row);
            }
            else Add(TitleLabel);
        }
    }

    public T Add<T>(T c) where T : Control
    {
        // Cards in one grid row share its height; the spare space lands in the last
        // row. Pin content to the top of its cell so nothing floats mid-card.
        if (c.Dock == DockStyle.None && (c.Anchor & (AnchorStyles.Top | AnchorStyles.Bottom)) == 0)
            c.Anchor |= AnchorStyles.Top;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(c, 0, RowCount);
        RowCount++;
        return c;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.CardBorder);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}

/// <summary>Label/value rows. Values wrap at the column width.</summary>
internal sealed class KvTable : TableLayoutPanel
{
    public KvTable()
    {
        ColumnCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        Margin = new Padding(0, 0, 0, 4);
        BackColor = Theme.Card;
    }

    public Label Add(string key, string initial = "…")
    {
        var k = Theme.Text(key, Theme.Small, Theme.Muted);
        k.Margin = new Padding(0, 4, 14, 3);
        var v = Theme.Text(initial, Theme.Body, Theme.Fg, wrap: true);
        v.Margin = new Padding(0, 2, 0, 2);
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(k, 0, RowCount);
        Controls.Add(v, 1, RowCount);
        RowCount++;
        return v;
    }
}

internal sealed class StatusDot : Control
{
    private Color _color = Theme.Unknown;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color DotColor
    {
        get => _color;
        set { if (_color != value) { _color = value; Invalidate(); } }
    }

    public StatusDot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Size = new Size(12, 12);
        TabStop = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var d = Math.Min(Width, Height) - 1;
        using var b = new SolidBrush(_color);
        e.Graphics.FillEllipse(b, (Width - d) / 2f, (Height - d) / 2f, d, d);
    }
}

/// <summary>A rounded status badge.</summary>
internal sealed class Pill : Control
{
    private Color _fill = Theme.Unknown;

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Fill
    {
        get => _fill;
        set { if (_fill != value) { _fill = value; Invalidate(); } }
    }

    public Pill()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        ForeColor = Theme.Bg;
        Font = Theme.SmallBold;
        AutoSize = true;
        TabStop = false;
        Margin = new Padding(0, 2, 8, 2);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = TextRenderer.MeasureText(string.IsNullOrEmpty(Text) ? " " : Text, Font);
        return new Size(s.Width + s.Height, s.Height + s.Height / 3);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = FlatButton.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), (Height - 1) / 2f))
        using (var b = new SolidBrush(_fill))
            g.FillPath(b, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>A sidebar entry: glyph + Thai label, accent bar when selected.</summary>
internal sealed class NavButton : Control
{
    private bool _selected, _hover;
    public string Glyph { get; }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Invalidate(); } }
    }

    public NavButton(string text, string glyph)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Text = text;
        Glyph = glyph;
        Font = Theme.Body;
        ForeColor = Theme.Fg;
        BackColor = Theme.Sidebar;
        Height = 42;
        Dock = DockStyle.Top;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PageTab;
        AccessibleName = text;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_selected ? Theme.Selected : _hover ? Color.FromArgb(18, 25, 44) : Theme.Sidebar);
        int bar = Math.Max(3, Height / 14);
        if (_selected)
            using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, 0, 0, bar, Height);

        int x = bar + Height / 3;
        var color = _selected ? Color.White : Theme.Fg;
        if (Theme.HasGlyphFont && Glyph.Length > 0)
        {
            var gs = TextRenderer.MeasureText(Glyph, Theme.Glyph);
            TextRenderer.DrawText(g, Glyph, Theme.Glyph, new Rectangle(x, 0, gs.Width, Height), _selected ? Theme.Accent : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            x += gs.Width + Height / 5;
        }
        TextRenderer.DrawText(g, Text, _selected ? Theme.Bold : Font, new Rectangle(x, 0, Width - x - 4, Height), color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues)
        {
            var r = ClientRectangle;
            r.Inflate(-2, -2);
            ControlPaint.DrawFocusRectangle(g, r, Color.White, Theme.Selected);
        }
    }
}

/// <summary>A full-width strip under the header (read-only mode, new manager version).</summary>
internal sealed class Banner : TableLayoutPanel
{
    public Label Message { get; }
    public FlatButton Action { get; }

    public Banner(Color back, string text, string actionText, EventHandler onAction)
    {
        BackColor = back;
        ColumnCount = 2;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        RowCount = 1;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Padding = new Padding(16, 6, 12, 2);
        Margin = Padding.Empty;
        Message = Theme.Text(text, Theme.Bold, Color.White, wrap: true);
        Message.Margin = new Padding(0, 5, 12, 5);
        Action = Theme.Button(actionText, ControlPaint.Dark(back, 0.1f), onAction);
        Controls.Add(Message, 0, 0);
        Controls.Add(Action, 1, 0);
        Visible = false;
    }
}
