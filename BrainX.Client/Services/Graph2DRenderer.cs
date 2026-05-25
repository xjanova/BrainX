using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using BrainX.Core.Models;

namespace BrainX.Client.Services;

/// <summary>
/// Canvas-style 2D graph view backed by a <see cref="WriteableBitmap"/>.
///
/// Why not <see cref="DrawingContext"/>? WPF's retained-mode 2D path
/// allocates and re-tessellates geometry for every <c>DrawEllipse</c> and
/// <c>DrawLine</c> call. With 450+ nodes and 2,700+ edges that turned into
/// ~5 FPS on the Brain Graph view. Obsidian's own graph runs on HTML5
/// Canvas — direct pixel writes, no scene graph — for exactly this reason.
/// We do the same here: lock the back buffer, fill it with our own
/// rasterizer (Bresenham line + filled circle with alpha blending),
/// AddDirtyRect, blit.
///
/// Same <see cref="PhysicsEngine"/> drives both 2D and 3D views; only the
/// projection layer differs. Z is dropped (orthographic).
/// </summary>
public class Graph2DRenderer : FrameworkElement
{
    public PhysicsEngine? Physics { get; set; }
    public int? SelectedIndex { get; set; }

    /// <summary>World-space point the camera is centered on.</summary>
    public Point ViewCenter { get; set; } = new(0, 0);

    /// <summary>Pixels per world unit — bigger = zoomed in.</summary>
    public double Scale { get; set; } = 30;

    /// <summary>
    /// Live arcs to paint over the edges, owned by MainWindow. Each entry is
    /// (sourceId, targetId, ageNormalized 0..1, tint).
    /// </summary>
    public List<(string srcId, string tgtId, double t, Color tint)>? Arcs { get; set; }


    private Color _accent = Color.FromRgb(0, 240, 255);
    private Color _secondary = Color.FromRgb(139, 92, 246);

    /// <summary>
    /// Optional category → color mapping. When set, nodes are filled by
    /// their <see cref="KnowledgeCategory"/> so the user can scan the
    /// brain and see at a glance which areas own which regions.
    /// </summary>
    public Func<KnowledgeCategory, Color>? CategoryColorFn { get; set; }

    public static readonly DependencyProperty BackgroundProperty =
        DependencyProperty.Register(
            nameof(Background), typeof(Brush), typeof(Graph2DRenderer),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Background
    {
        get => GetValue(BackgroundProperty) as Brush;
        set => SetValue(BackgroundProperty, value);
    }

    public void SetTheme(Color accent, Color secondary)
    {
        _accent = accent;
        _secondary = secondary;
        InvalidateVisual();
    }

    // ── Coordinate helpers ─────────────────────────────────────────
    public Point WorldToScreen(Point3D p) => new(
        ActualWidth / 2 + (p.X - ViewCenter.X) * Scale,
        ActualHeight / 2 - (p.Y - ViewCenter.Y) * Scale);

    public Point ScreenToWorld(Point p) => new(
        (p.X - ActualWidth / 2) / Scale + ViewCenter.X,
        -(p.Y - ActualHeight / 2) / Scale + ViewCenter.Y);

    // ── Pre-frame setup ────────────────────────────────────────────
    public void FitToContent(double margin = 1.2)
    {
        if (Physics == null || Physics.Nodes.Count == 0) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var n in Physics.Nodes)
        {
            var p = n.Position;
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }
        var spanX = Math.Max(1.0, maxX - minX);
        var spanY = Math.Max(1.0, maxY - minY);
        ViewCenter = new Point((minX + maxX) / 2, (minY + maxY) / 2);
        var sx = ActualWidth / (spanX * margin);
        var sy = ActualHeight / (spanY * margin);
        Scale = Math.Min(sx, sy);
        if (Scale < 4) Scale = 4;
        if (Scale > 200) Scale = 200;
    }

    public int? HitTest(Point screenPoint)
    {
        if (Physics == null) return null;
        var nodes = Physics.Nodes;
        int bestI = -1;
        double bestD2 = double.MaxValue;
        for (int i = 0; i < nodes.Count; i++)
        {
            var sp = WorldToScreen(nodes[i].Position);
            var dx = sp.X - screenPoint.X;
            var dy = sp.Y - screenPoint.Y;
            var d2 = dx * dx + dy * dy;
            // Generous click target: r + 12 px so dense clusters are
            // still actionable. With 450 nodes packed into a 400 px
            // sphere, the old r+4 rule made clicks bounce off gaps.
            var r = NodeRadius(nodes[i]) + 12;
            if (d2 < r * r && d2 < bestD2)
            {
                bestD2 = d2;
                bestI = i;
            }
        }
        return bestI < 0 ? null : bestI;
    }

    private static double NodeRadius(PhysicsNode n)
    {
        var baseR = 3.0 + Math.Min(8.0, Math.Log(1 + n.WordCount) * 0.7);
        return baseR + n.AccessIntensity * 4.0;
    }

    // ── Bitmap state ────────────────────────────────────────────────
    private WriteableBitmap? _bmp;
    private readonly Dictionary<string, int> _idxBuf = new(512);
    // Per-frame "which edges are blinking right now" lookup. Key is the
    // canonical (lo-idx, hi-idx) tuple of the two node indices; value is
    // the arc's age (0..1) plus the tint colour for the glow pass.
    private readonly Dictionary<(int, int), (double t, Color tint)> _activeEdgeMap = new();
    private readonly HashSet<int> _activeNodeSet = new();

    // ─── Node-glow fade-out (per-frame ageing keyed by node ID) ────────
    // When a node is hit by an arc we set its remaining fade time to
    // NodeFadeDurationSec. Each subsequent frame we subtract the wall-clock
    // delta so the halo dims gracefully instead of blinking off the moment
    // the arc finishes. Keyed by Id (string) — physics index is unstable
    // when nodes are added/removed between frames.
    private readonly Dictionary<string, double> _nodeFadeRemaining = new(64);
    private DateTime _lastFadeTickUtc = DateTime.UtcNow;
    /// <summary>How long after an arc-touch the halo takes to fully fade.
    /// 1.2s reads as "I just lit up" without leaving stale ghosts on busy
    /// graphs where 5+ arcs fire per second.</summary>
    private const double NodeFadeDurationSec = 1.2;

    /// <summary>True while at least one node is mid-fade. The MainWindow's
    /// per-frame redraw gate consults this so an idle physics graph still
    /// gets InvalidateVisual'd until every halo has fully dimmed.</summary>
    public bool HasActiveFade => _nodeFadeRemaining.Count > 0;

    /// <summary>Premultiplied BGRA32 packed as 0xAARRGGBB.</summary>
    private static uint Premultiplied(byte a, byte r, byte g, byte b)
    {
        // For PixelFormats.Pbgra32 the components are stored as
        // [B, G, R, A] in memory; treating as little-endian uint gives
        // 0xAARRGGBB. Color values must be premultiplied by alpha.
        var pa = a;
        var pr = (byte)(r * a / 255);
        var pg = (byte)(g * a / 255);
        var pb = (byte)(b * a / 255);
        return ((uint)pa << 24) | ((uint)pr << 16) | ((uint)pg << 8) | pb;
    }

    private static uint PackPbgra(Color c) => Premultiplied(c.A, c.R, c.G, c.B);
    private static uint PackPbgra(byte a, Color c) => Premultiplied(a, c.R, c.G, c.B);

    protected override unsafe void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var dipW = ActualWidth;
        var dipH = ActualHeight;
        if (dipW <= 0 || dipH <= 0) return;

        int pxW = Math.Max(1, (int)Math.Round(dipW));
        int pxH = Math.Max(1, (int)Math.Round(dipH));

        if (_bmp == null || _bmp.PixelWidth != pxW || _bmp.PixelHeight != pxH)
            _bmp = new WriteableBitmap(pxW, pxH, 96, 96, PixelFormats.Pbgra32, null);

        _bmp.Lock();
        try
        {
            byte* buf = (byte*)_bmp.BackBuffer.ToPointer();
            int stride = _bmp.BackBufferStride;

            // ── Clear ──
            // Default to fully TRANSPARENT so the parent Border's
            // background (the starfield image brush) shows through.
            // Only paint a solid colour if Background was explicitly set
            // to a SolidColorBrush — that's the only case where the host
            // wants us to own the backdrop.
            uint clearPx = 0x00000000;
            if (Background is SolidColorBrush sb)
                clearPx = PackPbgra(sb.Color);
            FillBuffer(buf, stride, pxW, pxH, clearPx);

            if (Physics == null || Physics.Nodes.Count == 0)
            {
                _bmp.AddDirtyRect(new Int32Rect(0, 0, pxW, pxH));
                // Don't unlock here — the finally below owns the unlock.
                // Calling it twice throws "image is unlocked".
                dc.DrawImage(_bmp, new Rect(0, 0, dipW, dipH));
                return;
            }

            var nodes = Physics.Nodes;

            // Reusable id → idx dictionary (no per-frame allocation).
            _idxBuf.Clear();
            for (int i = 0; i < nodes.Count; i++) _idxBuf[nodes[i].Id] = i;

            // ── Active set from arcs ──
            // Convert the Arcs list (which arrived as opaque tuples)
            // into two fast lookups:
            //   1. activeEdgeMap: which edges should blink white this frame
            //   2. activeNodes:   which nodes should glow this frame
            // Both rebuilt every frame because arcs can finish mid-frame
            // and re-spawn on user clicks.
            _activeEdgeMap.Clear();
            _activeNodeSet.Clear();

            // Wall-clock delta for fade-out. Clamped at 0.5s so a frame
            // stall (e.g. window minimised then restored) doesn't snap
            // every halo to zero in one frame.
            var nowUtc = DateTime.UtcNow;
            double dt = Math.Min(0.5, (nowUtc - _lastFadeTickUtc).TotalSeconds);
            _lastFadeTickUtc = nowUtc;

            if (Arcs != null)
            {
                foreach (var (srcId, tgtId, t, tint) in Arcs)
                {
                    if (t < 0 || t >= 1.0) continue;
                    if (!_idxBuf.TryGetValue(srcId, out var si)) continue;
                    if (!_idxBuf.TryGetValue(tgtId, out var ti)) continue;
                    // Canonical key — edges are bidirectional in this view
                    var key = si < ti ? (si, ti) : (ti, si);
                    // If multiple arcs cover the same edge, keep the
                    // brightest (lowest t, since brightness fades over t).
                    if (!_activeEdgeMap.TryGetValue(key, out var prev) || t < prev.t)
                        _activeEdgeMap[key] = (t, tint);
                    _activeNodeSet.Add(si);
                    _activeNodeSet.Add(ti);
                }
            }

            // ─── Fade-out bookkeeping ──────────────────────────────────
            // Refresh fade timer for any node currently touched by a live
            // arc, then decay every tracked entry by the wall-clock delta.
            // Removed when remaining ≤ 0 so the dictionary never grows
            // unbounded on long sessions.
            foreach (var idx in _activeNodeSet)
            {
                if (idx >= 0 && idx < nodes.Count)
                    _nodeFadeRemaining[nodes[idx].Id] = NodeFadeDurationSec;
            }
            if (_nodeFadeRemaining.Count > 0)
            {
                // Snapshot keys because we can't mutate a dictionary while iterating.
                var snapshot = _nodeFadeRemaining.Keys.ToArray();
                foreach (var id in snapshot)
                {
                    var rem = _nodeFadeRemaining[id] - dt;
                    if (rem <= 0) _nodeFadeRemaining.Remove(id);
                    else _nodeFadeRemaining[id] = rem;
                }
            }

            // ── Edges ──
            // Three layers (back-to-front):
            //   1. dim base — every edge at low alpha so the web is visible
            //   2. trail glow — edges with AccessIntensity > 0 paint a
            //      brighter cyan stroke whose alpha follows the intensity.
            //      This is the persistent "trail" for recently-used edges
            //      (plateau 2 s, then ~10 s fade — same scheme as nodes).
            //   3. arc-flash highlight — drawn after nodes, see further down
            uint wikiEdge = PackPbgra(80, _accent);
            uint autoEdge = PackPbgra(40, _secondary);
            var edges = Physics.Edges;
            for (int e = 0; e < edges.Count; e++)
            {
                var ed = edges[e];
                if (!_idxBuf.TryGetValue(ed.SourceId, out var si)) continue;
                if (!_idxBuf.TryGetValue(ed.TargetId, out var ti)) continue;
                var s = WorldToScreen(nodes[si].Position);
                var tgt = WorldToScreen(nodes[ti].Position);
                if ((s.X < 0 && tgt.X < 0) || (s.X > pxW && tgt.X > pxW)) continue;
                if ((s.Y < 0 && tgt.Y < 0) || (s.Y > pxH && tgt.Y > pxH)) continue;
                int sx = (int)s.X, sy = (int)s.Y, tx = (int)tgt.X, ty = (int)tgt.Y;

                // Layer 1 — dim base (always on).
                BlendLine(buf, stride, pxW, pxH, sx, sy, tx, ty,
                    ed.IsAuto ? autoEdge : wikiEdge);

                // Layer 2 — persistent trail glow if recently active.
                // Alpha curve: AccessIntensity ∈ [0..1] → extra alpha [0..150]
                // on top of the dim base, in the bright white-on-cyan range.
                if (ed.AccessIntensity > 0.05)
                {
                    byte glowA = (byte)(40 + ed.AccessIntensity * 150);
                    uint glowPx = PackPbgra(glowA,
                        Color.FromRgb(220, 245, 255));
                    BlendLine(buf, stride, pxW, pxH, sx, sy, tx, ty, glowPx);
                    // Second pixel-row stroke when intensity is in the
                    // plateau range so the trail reads as a 2-px ribbon
                    // before thinning out as it fades.
                    if (ed.AccessIntensity > 0.45)
                        BlendLine(buf, stride, pxW, pxH, sx, sy + 1, tx, ty + 1, glowPx);
                }
            }

            // ── Nodes ──
            // Two-pass paint: a translucent halo first, then the solid
            // core on top. Mimics the 3D mode's emissive-glow look so
            // balls don't read as "flat dots on a starfield" anymore.
            // Pre-resolve fill colours per category to avoid repeated
            // function calls inside the hot loop.
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                var p = WorldToScreen(n.Position);
                if (p.X < -20 || p.X > pxW + 20 || p.Y < -20 || p.Y > pxH + 20) continue;

                var lifeScale = n.BirthProgress * n.DeathProgress;
                var radius = NodeRadius(n) * lifeScale;
                if (radius < 0.5) continue;

                Color baseColor;
                if (n.AccessIntensity > 0.4) baseColor = Color.FromRgb(255, 224, 64);
                else if (i == SelectedIndex) baseColor = _secondary;
                else if (CategoryColorFn != null) baseColor = CategoryColorFn(n.Category);
                else baseColor = _accent;

                int cx = (int)p.X;
                int cy = (int)p.Y;

                // Active-node mega-halo with smooth fade-out.
                // While an arc is touching this node we paint at full
                // intensity. After the arc ends we keep painting for up
                // to NodeFadeDurationSec, scaling the halo's alpha by the
                // remaining fade ratio. The radius bonus also shrinks
                // proportionally so a "dimming + retreating" cue reads
                // naturally rather than the halo just thinning in place.
                if (_nodeFadeRemaining.TryGetValue(n.Id, out var fadeRem))
                {
                    // 0 = invisible, 1 = freshly active. Square the curve
                    // so the halo lingers visibly then drops off quickly
                    // at the tail (looks more organic than linear).
                    double fade = fadeRem / NodeFadeDurationSec;
                    fade = fade * fade;
                    if (fade > 0.01)
                    {
                        byte outerA = (byte)(110 * fade);
                        byte innerA = (byte)(160 * fade);
                        double outerR = radius + 3.0 + 5.0 * fade;
                        double innerR = radius + 2.0 + 3.0 * fade;
                        FillCircle(buf, stride, pxW, pxH, cx, cy, outerR,
                            PackPbgra(outerA, Color.FromRgb(255, 255, 255)));
                        FillCircle(buf, stride, pxW, pxH, cx, cy, innerR,
                            PackPbgra(innerA, baseColor));
                    }
                }

                // Outer halo — bigger, semi-transparent, blends with the
                // starfield so each ball reads as a "lit" sphere rather
                // than a flat sticker.
                FillCircle(buf, stride, pxW, pxH, cx, cy, radius + 2.5,
                    PackPbgra(70, baseColor));

                // Solid core — slightly brightened to compensate for the
                // halo dimming the eye's perception. Pure overwrite so
                // there's no blend-fade in the centre.
                Color coreColor = Color.FromRgb(
                    (byte)Math.Min(255, baseColor.R + 25),
                    (byte)Math.Min(255, baseColor.G + 25),
                    (byte)Math.Min(255, baseColor.B + 25));
                FillCircle(buf, stride, pxW, pxH, cx, cy, radius,
                    PackPbgra(255, coreColor));

                // Specular pip — small white dot offset toward the
                // upper-left, like a glossy sphere highlight. Only on
                // balls big enough to hold one (≥ 4 px radius) so dots
                // don't get overwhelmed.
                if (radius >= 4.0)
                {
                    int hx = cx - (int)(radius * 0.35);
                    int hy = cy - (int)(radius * 0.35);
                    FillCircle(buf, stride, pxW, pxH, hx, hy,
                        Math.Max(1.0, radius * 0.22),
                        PackPbgra(180, Color.FromRgb(255, 255, 255)));
                }

                if (i == SelectedIndex)
                {
                    // Halo ring — 1.6 px stroke approximated as a
                    // thicker circle outline by drawing two concentric
                    // circles and subtracting.
                    StrokeCircle(buf, stride, pxW, pxH,
                        cx, cy, radius + 3,
                        PackPbgra(255, _secondary), thickness: 2);
                }
            }

            // ── Active edge highlight pass ──
            // Drawn AFTER nodes so the bright lit edges sit ON TOP of
            // the balls — otherwise the dense cluster covers the lines
            // entirely. User asked for thinner: just 2-pixel-tall bright
            // line with NO transparency, no fat glow halo.
            foreach (var (key, info) in _activeEdgeMap)
            {
                var (si, ti) = key;
                var s = WorldToScreen(nodes[si].Position);
                var tgt = WorldToScreen(nodes[ti].Position);
                if ((s.X < 0 && tgt.X < 0) || (s.X > pxW && tgt.X > pxW)) continue;
                if ((s.Y < 0 && tgt.Y < 0) || (s.Y > pxH && tgt.Y > pxH)) continue;

                // Pulse: 3 blinks across the lifetime, never falling
                // below 0.85 so the line stays solid-bright; a faint
                // dip is enough to read as a blink without the line
                // ghosting through the background.
                double pulse = 0.85 + 0.15 * Math.Abs(Math.Sin(info.t * Math.PI * 3));
                byte coreA = (byte)(255 * pulse);
                uint corePx = PackPbgra(coreA, Color.FromRgb(255, 255, 255));

                int x0 = (int)s.X, y0 = (int)s.Y;
                int x1 = (int)tgt.X, y1 = (int)tgt.Y;
                // Solid white line, 2 pixels tall — no glow halo.
                BlendLine(buf, stride, pxW, pxH, x0, y0,     x1, y1,     corePx);
                BlendLine(buf, stride, pxW, pxH, x0, y0 + 1, x1, y1 + 1, corePx);
            }

            _bmp.AddDirtyRect(new Int32Rect(0, 0, pxW, pxH));
        }
        finally
        {
            _bmp.Unlock();
        }

        dc.DrawImage(_bmp, new Rect(0, 0, dipW, dipH));
    }

    // ────────────────────────────────────────────────────────────────
    //  Pixel-level rasterizers
    // ────────────────────────────────────────────────────────────────

    /// <summary>Solid fill — bgra is the literal pixel value.</summary>
    private static unsafe void FillBuffer(byte* buf, int stride, int w, int h, uint bgra)
    {
        for (int y = 0; y < h; y++)
        {
            uint* row = (uint*)(buf + y * stride);
            for (int x = 0; x < w; x++) row[x] = bgra;
        }
    }

    /// <summary>
    /// Filled circle, source-over alpha blending. Uses the implicit
    /// equation x²+y² ≤ r² and only iterates the bounding box. The
    /// "+0.5" anti-pattern (pretending we're sampling cell centers)
    /// gives slightly nicer-looking small circles without true AA.
    /// </summary>
    private static unsafe void FillCircle(byte* buf, int stride, int w, int h,
        int cx, int cy, double r, uint srcBgra)
    {
        if (r < 0.5) return;
        int rInt = (int)Math.Ceiling(r);
        int x0 = Math.Max(0, cx - rInt);
        int x1 = Math.Min(w - 1, cx + rInt);
        int y0 = Math.Max(0, cy - rInt);
        int y1 = Math.Min(h - 1, cy + rInt);
        double rSqr = r * r;

        // Decode source PBGRA once.
        byte sa = (byte)((srcBgra >> 24) & 0xFF);
        if (sa == 0) return;

        if (sa == 255)
        {
            // Opaque fast path — straight pixel write.
            for (int y = y0; y <= y1; y++)
            {
                int dy = y - cy;
                int dySqr = dy * dy;
                uint* row = (uint*)(buf + y * stride);
                for (int x = x0; x <= x1; x++)
                {
                    int dx = x - cx;
                    if (dx * dx + dySqr > rSqr) continue;
                    row[x] = srcBgra;
                }
            }
            return;
        }

        // Translucent — source-over composite.
        byte sr = (byte)((srcBgra >> 16) & 0xFF);
        byte sg = (byte)((srcBgra >> 8) & 0xFF);
        byte sb = (byte)(srcBgra & 0xFF);
        int invA = 255 - sa;

        for (int y = y0; y <= y1; y++)
        {
            int dy = y - cy;
            int dySqr = dy * dy;
            uint* row = (uint*)(buf + y * stride);
            for (int x = x0; x <= x1; x++)
            {
                int dx = x - cx;
                if (dx * dx + dySqr > rSqr) continue;
                uint dst = row[x];
                byte da = (byte)((dst >> 24) & 0xFF);
                byte dr = (byte)((dst >> 16) & 0xFF);
                byte dg = (byte)((dst >> 8) & 0xFF);
                byte db = (byte)(dst & 0xFF);
                byte ra = (byte)(sa + da * invA / 255);
                byte rr = (byte)(sr + dr * invA / 255);
                byte rg = (byte)(sg + dg * invA / 255);
                byte rb = (byte)(sb + db * invA / 255);
                row[x] = ((uint)ra << 24) | ((uint)rr << 16) | ((uint)rg << 8) | rb;
            }
        }
    }

    /// <summary>
    /// Stroked circle outline at the given thickness. Implemented as the
    /// difference of two filled circles (outer minus inner) — cheap and
    /// gives clean rings for the "selected node" halo.
    /// </summary>
    private static unsafe void StrokeCircle(byte* buf, int stride, int w, int h,
        int cx, int cy, double r, uint srcBgra, double thickness)
    {
        if (r < 0.5 || thickness < 0.5) return;
        int rInt = (int)Math.Ceiling(r + 0.5);
        int x0 = Math.Max(0, cx - rInt);
        int x1 = Math.Min(w - 1, cx + rInt);
        int y0 = Math.Max(0, cy - rInt);
        int y1 = Math.Min(h - 1, cy + rInt);
        double outerSqr = r * r;
        double innerR = r - thickness;
        double innerSqr = innerR * innerR;

        byte sa = (byte)((srcBgra >> 24) & 0xFF);
        if (sa == 0) return;
        byte sr = (byte)((srcBgra >> 16) & 0xFF);
        byte sg = (byte)((srcBgra >> 8) & 0xFF);
        byte sb = (byte)(srcBgra & 0xFF);
        int invA = 255 - sa;
        bool opaque = sa == 255;

        for (int y = y0; y <= y1; y++)
        {
            int dy = y - cy;
            int dySqr = dy * dy;
            uint* row = (uint*)(buf + y * stride);
            for (int x = x0; x <= x1; x++)
            {
                int dx = x - cx;
                int dSqr = dx * dx + dySqr;
                if (dSqr > outerSqr || dSqr < innerSqr) continue;
                if (opaque)
                {
                    row[x] = srcBgra;
                }
                else
                {
                    uint dst = row[x];
                    byte da = (byte)((dst >> 24) & 0xFF);
                    byte dr = (byte)((dst >> 16) & 0xFF);
                    byte dg = (byte)((dst >> 8) & 0xFF);
                    byte db = (byte)(dst & 0xFF);
                    byte ra = (byte)(sa + da * invA / 255);
                    byte rr = (byte)(sr + dr * invA / 255);
                    byte rg = (byte)(sg + dg * invA / 255);
                    byte rb = (byte)(sb + db * invA / 255);
                    row[x] = ((uint)ra << 24) | ((uint)rr << 16) | ((uint)rg << 8) | rb;
                }
            }
        }
    }

    /// <summary>
    /// Bresenham line with per-pixel alpha blending. Single-pixel-wide;
    /// good enough for graph edges where thickness wouldn't add much
    /// visual info on a dense graph anyway. Skips early when the segment
    /// is entirely off-screen.
    /// </summary>
    private static unsafe void BlendLine(byte* buf, int stride, int w, int h,
        int x0, int y0, int x1, int y1, uint srcBgra)
    {
        byte sa = (byte)((srcBgra >> 24) & 0xFF);
        if (sa == 0) return;

        int dx = Math.Abs(x1 - x0);
        int dy = Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx - dy;

        if (sa == 255)
        {
            while (true)
            {
                if ((uint)x0 < (uint)w && (uint)y0 < (uint)h)
                {
                    uint* row = (uint*)(buf + y0 * stride);
                    row[x0] = srcBgra;
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx)  { err += dx; y0 += sy; }
            }
            return;
        }

        byte sr = (byte)((srcBgra >> 16) & 0xFF);
        byte sg = (byte)((srcBgra >> 8) & 0xFF);
        byte sb = (byte)(srcBgra & 0xFF);
        int invA = 255 - sa;

        while (true)
        {
            if ((uint)x0 < (uint)w && (uint)y0 < (uint)h)
            {
                uint* row = (uint*)(buf + y0 * stride);
                uint dst = row[x0];
                byte da = (byte)((dst >> 24) & 0xFF);
                byte dr = (byte)((dst >> 16) & 0xFF);
                byte dg = (byte)((dst >> 8) & 0xFF);
                byte db = (byte)(dst & 0xFF);
                byte ra = (byte)(sa + da * invA / 255);
                byte rr = (byte)(sr + dr * invA / 255);
                byte rg = (byte)(sg + dg * invA / 255);
                byte rb = (byte)(sb + db * invA / 255);
                row[x0] = ((uint)ra << 24) | ((uint)rr << 16) | ((uint)rg << 8) | rb;
            }
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx)  { err += dx; y0 += sy; }
        }
    }

}
