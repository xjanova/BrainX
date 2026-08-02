/* BrainX Universe HUD — free layout: every readout becomes a window you place.
 *
 * The nine-area grid in hud.css is still the DEFAULT. It is what a fresh
 * install sees, it is what "Reset layout" returns to, and it is the only
 * arrangement that can promise no two panels collide before anyone has an
 * opinion. This file is the escape hatch: the moment the owner drags or
 * resizes anything, every panel is pinned at the exact rect the grid had just
 * handed it, and from then on each one carries its own position and size.
 *
 * Pinning ALL of them on the first gesture is the whole trick. Freeing only
 * the panel under the cursor would leave the other eight as grid items, and a
 * grid whose item count just changed re-flows — so the first pixel of a drag
 * would have thrown the other eight readouts into new corners. Freezing first
 * means the first pixel moves exactly one thing, and nothing else ever moves
 * unless it is dragged.
 *
 * Geometry is plain viewport pixels: #hud-layer is `position:fixed; inset:0`,
 * so an absolutely positioned child of it shares the viewport's coordinate
 * space and getBoundingClientRect() reads back exactly what we wrote.
 *
 * Everything here is gated on the caller — hud.js only invokes it under
 * ?hud=1. The wallpaper and dashboard-embed modes load this same page and
 * their overlays stay exactly as they were.
 */

/* Same shape as the other two on this page (obsidianx.wallpaper.prefs.v2,
   obsidianx.universe.settings.v3). The WebView2 user-data folder is stable
   across updates, so a layout placed today survives tomorrow's installer. */
const KEY = 'obsidianx.hud.layout.v1';

const MIN_W = 148;   // narrower than this and the title chip itself wraps
const MIN_H = 46;    // one title chip and nothing else — a deliberate "collapsed"
const EDGE = 9;      // px inside a border that counts as a resize grip
const SNAP = 8;      // magnetism to a viewport gutter or a neighbour's edge
const SLOP = 3;      // px of travel before a press counts as a drag, so clicks live

/** The nine grid areas, which double as the identity a saved rect is filed
 *  under. Reading the key off the class means the markup stays the single
 *  source of truth for which panel is which. */
const AREAS = ['tl', 'tc', 'tr', 'ml', 'mr', 'bl', 'bc', 'br'];

const CURSOR = {
    n: 'ns-resize', s: 'ns-resize', e: 'ew-resize', w: 'ew-resize',
    ne: 'nesw-resize', sw: 'nesw-resize', nw: 'nwse-resize', se: 'nwse-resize',
};

/* A press on one of these belongs to the control, not to the layout. The bus
   canvas is in the list because it runs its own drag (orbiting the solar
   system in agentbus3d.js) — two drags on one pointer means neither works. */
const CONTROLS = 'button, a, input, select, textarea, canvas, [role="button"]';

let panels = [];
let placed = false;          // has the layout been taken over by the owner?
let topZ = 10;
let view = { w: 0, h: 0 };   // viewport the current rects were authored against
let gesture = null;
let resetBtn = null;

// ── Public ───────────────────────────────────────────────────────

export function initHudLayout() {
    panels = [...document.querySelectorAll('#hud-layer > .hud-panel')];
    if (!panels.length) return;

    for (const p of panels) {
        p.dataset.hudKey = AREAS.find(a => p.classList.contains('hud-' + a)) || '';
        p.addEventListener('pointerdown', onDown);
        p.addEventListener('pointermove', onHover);
        p.addEventListener('pointerleave', onLeave);
        p.addEventListener('pointerup', onUp);
        p.addEventListener('pointercancel', onUp);
        p.addEventListener('dblclick', onFit);
    }

    wireReset();

    /* A window that changed shape has to be answered, or a layout authored on
       a maximised window leaves half its panels off a restored one — and there
       is no window list to get them back from. See reanchorAxis for the rule. */
    addEventListener('resize', onViewportResize);

    /* Safety net for a pointer that never comes back to the panel it started
       on. Capture normally guarantees it does, but capture is the one part of
       this that can fail — and the failure mode is the worst one available: a
       gesture that never ends, so every later movement of the mouse keeps
       dragging a panel nobody is holding. These fire second when capture DID
       work (the panel's own handler runs first and clears the gesture), so the
       healthy path is unaffected. onMove computes from the gesture's start rect
       rather than incrementally, which is what makes handling one move twice
       harmless. */
    addEventListener('pointermove', e => { if (gesture?.id === e.pointerId) onMove(e); });
    addEventListener('pointerup', onUp);
    addEventListener('pointercancel', onUp);

    view = { w: innerWidth, h: innerHeight };
    restore();
}

/** Hand every panel back to the grid. Exported so hud.js can offer it from
 *  anywhere; the visible control is the one wired in wireReset(). */
export function resetHudLayout() {
    for (const p of panels) {
        p.classList.remove('hud-free');
        // CSS names, not the camelCase aliases: removeProperty does no case
        // folding, so removeProperty('zIndex') silently removes nothing and
        // leaves a reset panel permanently stacked over its neighbours.
        for (const prop of ['left', 'top', 'width', 'height', 'z-index', 'cursor'])
            p.style.removeProperty(prop);
    }
    placed = false;
    topZ = 10;
    try { localStorage.removeItem(KEY); } catch { /* private mode */ }
    syncResetBtn();
}

// ── Freezing the grid ────────────────────────────────────────────

/**
 * Pin every VISIBLE panel at its current on-screen rect.
 *
 * All of them, not just the one being dragged. Each panel names its own grid
 * area, so leaving the others in the grid would not by itself move them — but
 * the grid still owns them, and the grid changes its mind: hud.css re-writes
 * grid-template-areas below 820px and drops .hud-ml/.hud-mr/.hud-br on a short
 * window. An arrangement the owner placed by hand should survive a window
 * resize intact, not have two thirds of itself re-arranged by a breakpoint. So
 * the first deliberate gesture takes the whole layout off the grid, once.
 *
 * Hidden panels are skipped: hud.css drops several at small window sizes, and
 * a display:none element measures 0×0 — pinning one would file a zero-size
 * rect and resurrect it later as an invisible sliver. They stay grid items and
 * are pinned by the next gesture after the window brings them back.
 */
function freezeAll() {
    markPlaced();
    for (const p of panels) freezeOne(p);
    view = { w: innerWidth, h: innerHeight };
}

function markPlaced() {
    if (placed) return;
    placed = true;
    syncResetBtn();
}

function freezeOne(p) {
    if (p.classList.contains('hud-free')) return;
    const r = p.getBoundingClientRect();
    if (r.width < 1 || r.height < 1) return;   // hidden by a media query
    p.classList.add('hud-free');
    writeRect(p, { x: r.left, y: r.top, w: r.width, h: r.height });
}

function writeRect(p, r) {
    p.style.left = Math.round(r.x) + 'px';
    p.style.top = Math.round(r.y) + 'px';
    p.style.width = Math.round(r.w) + 'px';
    p.style.height = Math.round(r.h) + 'px';
}

const readRect = p => ({
    x: parseFloat(p.style.left) || 0, y: parseFloat(p.style.top) || 0,
    w: parseFloat(p.style.width) || 0, h: parseFloat(p.style.height) || 0,
});

// ── Hit-testing ──────────────────────────────────────────────────

/** Which border the pointer is over, '' for the body of the panel.
 *
 *  Edge hit-testing rather than handle elements: .hud-panel scrolls its own
 *  content, and anything positioned inside a scroll box is placed against the
 *  padding box — a corner grip pinned to bottom:0 would sit at the bottom of
 *  the CONTENT and scroll out of reach the moment a feed grew. The border box
 *  never scrolls, so measuring against it always answers correctly. */
function dirAt(p, e) {
    const r = p.getBoundingClientRect();
    const x = e.clientX - r.left, y = e.clientY - r.top;
    const grip = Math.min(EDGE, r.height / 3, r.width / 3);
    let d = '';
    if (y <= grip) d = 'n'; else if (y >= r.height - grip) d = 's';
    if (x <= grip) d += 'w'; else if (x >= r.width - grip) d += 'e';
    return d;
}

const onControl = t => !!(t?.closest && t.closest(CONTROLS));

// ── Pointer gestures ─────────────────────────────────────────────

function onHover(e) {
    // Pointer capture routes every move during a gesture back to the panel it
    // started on, so this one listener is both the hover cursor and the drag.
    if (gesture) return void (gesture.id === e.pointerId && onMove(e));
    const p = e.currentTarget;
    const dir = dirAt(p, e);
    /* An edge outranks a control that happens to touch it — otherwise a panel
       whose buttons reach its border could never be resized from that side.
       Everywhere else the control keeps its own cursor. */
    const want = dir ? CURSOR[dir] : onControl(e.target) ? '' : 'move';
    /* Only on CHANGE. Writing the same value back still dirties inline style,
       which invalidates layout, which makes the getBoundingClientRect above
       force a fresh one on the very next move — a synchronous relayout per
       mouse event, on a page already running a three.js render loop. */
    if (p.style.cursor !== want) p.style.cursor = want;
}

function onLeave(e) {
    if (!gesture) e.currentTarget.style.removeProperty('cursor');
}

function onDown(e) {
    if (e.button !== 0) return;
    const p = e.currentTarget;
    raise(p);

    const dir = dirAt(p, e);
    if (!dir && onControl(e.target)) return;   // the control gets the press

    e.preventDefault();                        // no text selection while dragging
    e.stopPropagation();                       // and the camera never sees it
    /* Capture keeps the gesture attached to this panel when the pointer runs
       off it — which a drag does constantly, and a resize does the moment the
       panel gets smaller than the hand moving it. It throws when the pointer
       is no longer active (a lost button, a synthetic event); the gesture
       below still works without it, so the failure must not take the drag
       down with it. */
    try { p.setPointerCapture(e.pointerId); } catch { /* uncapturable pointer */ }

    /* The rect is read from getBoundingClientRect, not from the inline style,
       because on the very first gesture there IS no inline style yet — the
       panel is still a grid item and freezeAll() has not run. Deferring the
       freeze to the first actual MOVEMENT is what keeps a plain click on a
       feed row from silently converting the whole HUD to a manual layout. */
    const r = p.getBoundingClientRect();
    gesture = {
        p, dir, id: e.pointerId, live: false,
        px: e.clientX, py: e.clientY,
        start: { x: r.left, y: r.top, w: r.width, h: r.height },
        lines: null,
    };
}

function onMove(e) {
    const g = gesture;
    const dx = e.clientX - g.px, dy = e.clientY - g.py;

    if (!g.live) {
        // A resize is intentional from the first pixel — nothing else lives on
        // a border. A move has to clear the slop so clicks survive.
        if (!g.dir && Math.abs(dx) < SLOP && Math.abs(dy) < SLOP) return;
        g.live = true;
        freezeAll();
        // freezeAll may have shifted nothing, but it CAN have re-measured this
        // panel; re-read so the delta is applied to the rect actually on screen.
        g.start = readRect(g.p);
        g.lines = snapLines(g.p);
        g.p.classList.add('is-holding');
    }

    const s = g.start;
    let { x, y, w, h } = s;

    if (!g.dir) {
        x = s.x + dx; y = s.y + dy;
        if (!e.altKey) {
            x = snapSpan(x, w, g.lines.v);
            y = snapSpan(y, h, g.lines.h);
        }
    } else {
        if (g.dir.includes('e')) w = s.w + dx;
        if (g.dir.includes('s')) h = s.h + dy;
        if (g.dir.includes('w')) { x = s.x + dx; w = s.w - dx; }
        if (g.dir.includes('n')) { y = s.y + dy; h = s.h - dy; }

        if (!e.altKey) {
            // Only the edge under the pointer is magnetic. Snapping the far
            // side too would resize from both ends at once.
            if (g.dir.includes('e')) w = snapTo(x + w, g.lines.v) - x;
            if (g.dir.includes('s')) h = snapTo(y + h, g.lines.h) - y;
            if (g.dir.includes('w')) { const nx = snapTo(x, g.lines.v); w += x - nx; x = nx; }
            if (g.dir.includes('n')) { const ny = snapTo(y, g.lines.h); h += y - ny; y = ny; }
        }

        // Floors are enforced against the ANCHORED side, so shrinking past the
        // minimum from the west or north stops the edge instead of dragging the
        // whole panel backwards with it.
        if (w < MIN_W) { if (g.dir.includes('w')) x = s.x + s.w - MIN_W; w = MIN_W; }
        if (h < MIN_H) { if (g.dir.includes('n')) y = s.y + s.h - MIN_H; h = MIN_H; }
    }

    writeRect(g.p, { x, y, w, h });
}

function onUp() {
    const g = gesture;
    if (!g) return;
    gesture = null;
    g.p.classList.remove('is-holding');
    g.p.style.removeProperty('cursor');
    if (!g.live) return;             // it was a click after all — nothing to save
    keepReachable(g.p);
    save();
}

/** Double-click sizes a panel to its content — the one size the owner cannot
 *  find by dragging, because it is a number only the layout engine knows. */
function onFit(e) {
    const p = e.currentTarget;
    if (onControl(e.target)) return;
    freezeAll();

    const g = gutters();
    const r = readRect(p);
    p.style.height = 'auto';
    const want = Math.min(p.offsetHeight, innerHeight - g.t - g.b);
    writeRect(p, { ...r, h: Math.max(MIN_H, want) });
    keepReachable(p);
    save();
}

/** Raise on touch. Free panels can overlap by design, so the last one the
 *  owner reached for is the one that must be on top — and z-index here is
 *  scoped to #hud-layer's stacking context, so no panel can ever climb over
 *  the note card or the settings panel above it. */
function raise(p) {
    if (p.style.zIndex && +p.style.zIndex === topZ) return;
    p.style.zIndex = ++topZ;
}

// ── Snapping ─────────────────────────────────────────────────────

/** The layer's own gutter, measured off the element rather than parsed out of
 *  --hud-gap behind it: that variable holds a clamp(), and a custom property
 *  computes to its token stream, not to a length — parseFloat would answer
 *  NaN and every gutter snap would quietly land on the fallback instead of on
 *  the line the panels are actually sitting against. paddingBottom carries the
 *  reserved controls strip too, so the bottom line stays right without this
 *  file knowing how tall that strip is. */
function gutters() {
    const cs = getComputedStyle(document.getElementById('hud-layer'));
    return {
        l: parseFloat(cs.paddingLeft) || 0, r: parseFloat(cs.paddingRight) || 0,
        t: parseFloat(cs.paddingTop) || 0, b: parseFloat(cs.paddingBottom) || 0,
    };
}

/** Candidate alignment lines: the viewport, the HUD's own gutters, and every
 *  OTHER panel's edges. Aligning to a neighbour is what keeps a hand-placed
 *  HUD from reading as sloppy — the eye forgives overlap long before it
 *  forgives two panels three pixels out of line. Hold Alt to place freely. */
function snapLines(self) {
    const g = gutters();
    const v = [0, g.l, innerWidth - g.r, innerWidth];
    const h = [0, g.t, innerHeight - g.b, innerHeight];
    for (const p of panels) {
        if (p === self || !p.classList.contains('hud-free')) continue;
        const r = readRect(p);
        if (!r.w) continue;
        v.push(r.x, r.x + r.w);
        h.push(r.y, r.y + r.h);
    }
    return { v, h };
}

function snapTo(value, lines) {
    let best = value, dist = SNAP + 1;
    for (const l of lines) {
        const d = Math.abs(value - l);
        if (d < dist) { dist = d; best = l; }
    }
    return best;
}

/** Snap whichever of the two edges is closest to a line, and move the span as
 *  a unit — a dragged panel should be able to land flush by its right edge as
 *  easily as by its left. */
function snapSpan(start, size, lines) {
    let best = start, dist = SNAP + 1;
    for (const l of lines) {
        for (const off of [0, size]) {
            const d = Math.abs(start + off - l);
            if (d < dist) { dist = d; best = l - off; }
        }
    }
    return best;
}

// ── Staying on screen ────────────────────────────────────────────

/**
 * A panel dragged off the edge is not "hidden", it is LOST: there is no window
 * list to get it back from, and the only recovery would be Reset layout, which
 * throws away every other placement too. So a panel always keeps a graspable
 * amount of itself inside the viewport.
 */
function keepReachable(p) {
    const r = readRect(p);
    const keepX = Math.min(r.w, 90), keepY = Math.min(r.h, 34);
    const x = Math.min(Math.max(r.x, keepX - r.w), innerWidth - keepX);
    const y = Math.min(Math.max(r.y, 0), innerHeight - keepY);   // never above the top
    if (x !== r.x || y !== r.y) writeRect(p, { ...r, x, y });
}

function onViewportResize() {
    const from = view;
    view = { w: innerWidth, h: innerHeight };
    if (!placed || !from.w || !from.h) return;

    let moved = false;
    for (const p of panels) {
        // Panels still on the grid stay the grid's business — it already
        // handles a resize, and better than re-anchoring would.
        if (!p.classList.contains('hud-free')) continue;
        const r = readRect(p);
        if (!r.w) continue;
        writeRect(p, reanchor(r, from, view));
        keepReachable(p);
        moved = true;
    }
    if (moved) save();
}

/** Keep each panel where it was RELATIVE to the window, and shrink it only if
 *  the new viewport genuinely cannot hold it. */
function reanchor(r, from, to) {
    const w = Math.min(r.w, to.w), h = Math.min(r.h, to.h);
    return {
        x: reanchorAxis(r.x, w, from.w, to.w),
        y: reanchorAxis(r.y, h, from.h, to.h),
        w, h,
    };
}

/**
 * One axis of the re-anchor, in three cases.
 *
 * A panel parked against an edge keeps its exact distance from THAT edge —
 * that is what makes a corner readout stay in its corner at every window size,
 * and it is the case that matters most because most of them are in corners.
 *
 * A panel floating between the edges keeps its position as a FRACTION of the
 * window instead. Deciding by "which half is its centre in" looked equivalent
 * and was not: the top-centre panel sat one pixel right of centre, counted as
 * right-anchored, and a 1280→900 resize slid it 380 px to the left. Nothing
 * about a panel in the middle should be anchored to an edge 470 px away.
 *
 * The edge zone scales with the window so the same layout behaves the same way
 * on a laptop and on a 4K panel, with a floor for genuinely small windows.
 */
function reanchorAxis(pos, size, from, to) {
    const head = pos, tail = from - (pos + size);
    const zone = Math.max(120, from * 0.15);
    if (head <= zone && head <= tail) return head;             // pinned to the near edge
    if (tail <= zone && tail < head) return to - tail - size;  // pinned to the far edge
    return (pos + size / 2) / from * to - size / 2;            // floating: keep the fraction
}

// ── Persistence ──────────────────────────────────────────────────

function save() {
    if (!placed) return;
    const data = { w: innerWidth, h: innerHeight, panels: {} };
    for (const p of panels) {
        if (!p.classList.contains('hud-free') || !p.dataset.hudKey) continue;
        data.panels[p.dataset.hudKey] = readRect(p);
    }
    try { localStorage.setItem(KEY, JSON.stringify(data)); } catch { /* private mode */ }
}

/**
 * Apply a stored layout. Nothing here MEASURES a panel — a saved rect is
 * absolute, so this runs correctly the instant the DOM exists, before web
 * fonts land and before the first frame is composited. That matters: the
 * obvious implementation defers to requestAnimationFrame so the grid can
 * settle first, and rAF does not fire in a window that is not compositing —
 * a HUD that boots behind another view or on a minimised window would sit in
 * its default arrangement until something happened to show it.
 *
 * A panel with NO stored rect is deliberately left on the grid rather than
 * pinned at whatever it measures right now: mid-boot that measurement is
 * taken against fallback metrics, and freezing it would make a one-off font
 * swap permanent. It joins the free layout at the next gesture, by which time
 * the page has settled.
 */
function restore() {
    let data = null;
    try { data = JSON.parse(localStorage.getItem(KEY) || 'null'); } catch { /* corrupt */ }
    const saved = data?.panels;
    if (!saved || !Object.keys(saved).length) return;

    markPlaced();
    const from = { w: data.w || view.w, h: data.h || view.h };
    for (const p of panels) {
        const r = saved[p.dataset.hudKey];
        if (!r?.w || !r?.h) continue;
        p.classList.add('hud-free');
        writeRect(p, reanchor(r, from, view));
        keepReachable(p);
    }
}

// ── The way back ─────────────────────────────────────────────────

function wireReset() {
    resetBtn = document.getElementById('hud-layout-reset');
    resetBtn?.addEventListener('click', resetHudLayout);
    syncResetBtn();
}

/** The button only exists once there is something to undo. A permanent "Reset
 *  layout" on a HUD nobody has touched is a control that does nothing. */
function syncResetBtn() {
    if (resetBtn) resetBtn.hidden = !placed;
    document.body.classList.toggle('hud-placed', placed);
}
