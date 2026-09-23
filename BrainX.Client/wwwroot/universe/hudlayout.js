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
 * A window that changes shape never rewrites what the owner placed. Each
 * arrangement is filed under the window size it was made at, and every other
 * size is DERIVED from the nearest one by hudplace.js — anchors kept, cards
 * that were apart kept apart, nothing pushed out of the gutters. Deriving
 * afresh each time is what makes maximise → restore → maximise land exactly
 * where it started; the first version re-anchored the current rects and saved
 * them, so every resize quietly clamped the layout a little more. And because
 * arrangements are per size, the owner can arrange a half-screen window
 * differently from a maximised one without losing either.
 *
 * Geometry is plain viewport pixels: #hud-layer is `position:fixed; inset:0`,
 * so an absolutely positioned child of it shares the viewport's coordinate
 * space and getBoundingClientRect() reads back exactly what we wrote.
 *
 * Everything here is gated on the caller — hud.js only invokes it under
 * ?hud=1. The wallpaper and dashboard-embed modes load this same page and
 * their overlays stay exactly as they were.
 */

import { MIN_W, MIN_H, planLayout, mapRect, findRoom, reachable, overlaps } from './hudplace.js';

/* Same shape as the other two on this page (obsidianx.wallpaper.prefs.v2,
   obsidianx.universe.settings.v3). The WebView2 user-data folder is stable
   across updates, so a layout placed today survives tomorrow's installer.
   Still ".v1" although the value grew a `layouts` list: the old fields are
   kept alongside it, so a downgrade reads the latest arrangement instead of
   finding its key empty. */
const KEY = 'obsidianx.hud.layout.v1';

const EDGE = 9;      // px inside a border that counts as a resize grip
const SNAP = 8;      // magnetism to a viewport gutter or a neighbour's edge
const SLOP = 3;      // px of travel before a press counts as a drag, so clicks live

/* Two windows within 5% of each other on both axes are the same size — the
   taskbar auto-hiding, a scrollbar, a DPI rounding. Arranging at one of them
   edits the arrangement already filed for that size instead of starting a new
   one. Past that it is a genuinely different window (half-screen, a second
   monitor) and gets its own, up to MAX_LAYOUTS, least recently seen dropped
   first. */
const SAME_SIZE = 0.05;
const MAX_LAYOUTS = 6;

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
let gesture = null;
let resetBtn = null;

/** What the owner placed: [{ w, h, g, used, seen, panels: { key: rect } }],
 *  one per window size they have arranged at. `g` is that window's gutters —
 *  the gap is a clamp() of the width, so it is part of what "flush" meant
 *  there. `seen` is which cards were on screen when it was filed (see
 *  relayout). */
let layouts = [];
/** Keys on screen at the last relayout, so a card that has just appeared can
 *  be told from one that was there all along. null until the first one. */
let shown = null;
/** Orders `used` stamps. Wall-clock time alone ties when a resize and a drop
 *  land in the same millisecond, and can run backwards when the clock is
 *  corrected — either would make "the arrangement in use" ambiguous. */
let clock = 0;
const tick = () => (clock = Math.max(clock + 1, Date.now()));

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
       is no window list to get them back from. hudplace.js has the rules. */
    addEventListener('resize', () => relayout());

    /* A card switched on or off from the settings panel. Off gives its
       neighbours their room back; on takes it again — and if something was
       arranged over its spot while it was away, it looks for free room rather
       than landing on top. */
    const switches = new MutationObserver(() => relayout());
    for (const p of panels) switches.observe(p, { attributes: true, attributeFilter: ['hidden'] });

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
    layouts = [];
    shown = null;
    clock = 0;
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
 * rect and resurrect it later as an invisible sliver. They stay grid items
 * until the window brings them back, and then relayout() finds each one free
 * room among the placed cards rather than pinning it wherever the grid had
 * put it — which, with the grid no longer holding anything else, is usually
 * on top of a card the owner placed.
 */
function freezeAll() {
    if (placed) return void relayout();
    markPlaced();
    for (const p of panels) freezeOne(p);
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
        freezeAll();       // before `live`: relayout() stands aside for a live gesture
        g.live = true;
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
    /* The window changed shape, or a card was switched, while this one was in
       the hand — and relayout() stood aside, so the others are still where
       the OLD window put them. Filing that would record a layout nobody made.
       Place them for this window, keep the held card where it was dropped,
       then file. */
    if (g.stale) {
        const dropped = readRect(g.p);
        relayout();
        writeRect(g.p, dropped);
        keepReachable(g.p);
    }
    commit();
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
    commit();
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

/** A card let go of past an edge keeps a graspable amount of itself on
 *  screen — see reachable() in hudplace.js for why that is not optional. */
function keepReachable(p) {
    const r = readRect(p);
    const k = reachable(r, { w: innerWidth, h: innerHeight });
    if (k.x !== r.x || k.y !== r.y) writeRect(p, k);
}

// ── Arrangements ─────────────────────────────────────────────────

/** How far apart two window sizes are, as ratios: 960 → 1920 is as far as
 *  1920 → 3840, which is how different they look. */
const distance = (l, w, h) => Math.abs(Math.log(w / l.w)) + Math.abs(Math.log(h / l.h));

function nearest(w, h, skip = null, key = null) {
    let best = null, bestD = Infinity;
    for (const l of layouts) {
        if (l === skip || (key && !l.panels[key])) continue;
        const d = distance(l, w, h);
        if (d < bestD) { bestD = d; best = l; }
    }
    return best;
}

const frameOf = (l, fallback) => ({ w: l.w, h: l.h, g: l.g || fallback.g });

/** A card as the owner placed it, in `base`'s window — or, if they only ever
 *  placed it at another size (it was hidden when they arranged this one),
 *  carried across from the nearest size that has it. */
function designOf(key, base, from) {
    if (base.panels[key]) return base.panels[key];
    const other = nearest(from.w, from.h, base, key);
    return other ? mapRect(other.panels[key], frameOf(other, from), from) : null;
}

/**
 * The gutters a card is planned inside: the layer's own padding, except the
 * top. A notice banner inflates padding-top while it is up, and an
 * arrangement must not be re-planned around a banner that is about to go — so
 * the top gutter is the plain gap, which padding-left always is.
 */
function lane() {
    const cs = getComputedStyle(document.getElementById('hud-layer'));
    const gap = parseFloat(cs.paddingLeft) || 0;
    return { l: gap, r: parseFloat(cs.paddingRight) || 0, t: gap, b: parseFloat(cs.paddingBottom) || 0 };
}

// Hidden by its own switch ([hidden]) or by a breakpoint in hud.css.
const isShown = p => getComputedStyle(p).display !== 'none';

/**
 * Put every placed card where the current window says it goes. Runs on every
 * resize, on a card switched on or off, and once at boot — and always derives
 * from what the owner placed, never from what is on screen, so it can run any
 * number of times without drifting.
 *
 * It only WRITES the arrangement in two cases, both about a card that was not
 * on screen when the owner arranged the rest: one coming back onto a spot
 * that something else was arranged over while it was away, and one the grid
 * is still holding. Each is given free room among the others, and that room
 * is filed as its place.
 *
 * "Not on screen when the owner arranged the rest" is recorded, not guessed:
 * each arrangement keeps the keys that were visible when it was filed
 * (`seen`). Two cards the owner could SEE overlapping were stacked on
 * purpose, and stay stacked however often a breakpoint hides one of them.
 */
function relayout() {
    if (!placed) return;
    // A card in the hand is the owner's; onUp catches the others up after.
    if (gesture?.live) return void (gesture.stale = true);
    const W = innerWidth, H = innerHeight;
    /* A minimised or collapsed host reports a window of nothing. Planning
       against that would crush every card to its floor — and since the next
       real size is derived from the arrangement anyway, waiting costs nothing.
       The first version did plan against it, and a HUD booted minimised came
       up with every card 0×0 and no way to grab one. */
    if (W < 200 || H < 150) return;
    const base = nearest(W, H);
    if (!base) return;
    base.used = tick();
    const to = { w: W, h: H, g: lane() };
    const from = frameOf(base, to);
    /* The first real pass cannot adopt the grid's cards (see `stragglers`
       below), so it books a second one for when the page has settled. A
       timer, not rAF: rAF does not fire in a window that is not compositing.
       Booked here rather than at boot because a HUD booted minimised has its
       first real pass whenever the window is finally shown. */
    if (!shown) setTimeout(() => relayout(), 1500);

    const cards = [];
    for (const p of panels) {
        const key = p.dataset.hudKey;
        if (!key || !p.classList.contains('hud-free')) continue;
        const rect = designOf(key, base, from);
        if (rect) cards.push({ key, p, rect, shown: isShown(p) });
    }

    // Unseen: carried in from another size, or hidden when this arrangement
    // was filed. An arrangement from before `seen` existed counts every card
    // it holds as seen — the old behaviour, never a surprise move.
    for (const c of cards) c.seen = !!base.panels[c.key] && (!base.seen || base.seen.includes(c.key));
    const arriving = cards.filter(c => c.shown && !shown?.has(c.key) && !c.seen
        && cards.some(o => o !== c && o.shown && overlaps(o.rect, c.rect)));
    // A card the grid still holds has to be MEASURED to be adopted, and at
    // boot that would be measuring fallback fonts — so not on the first pass.
    const stragglers = shown
        ? panels.filter(p => p.dataset.hudKey && !p.classList.contains('hud-free') && isShown(p))
        : [];
    /* A card given a spot here is filed there but stays UNSEEN until the owner
       files an arrangement themselves. The spot was free in this window, and
       mapping it into the arrangement's own window is not guaranteed to keep
       it free — so if a neighbour ever lands on it, the planner parts them
       rather than reading the overlap as something the owner chose. With no
       free room at all the card keeps its own spot, on the same terms. */
    const file = (key, rect) => (base.panels[key] = mapRect(rect, to, from));
    if (arriving.length || stragglers.length) {
        for (const c of arriving) c.shown = false;
        const first = planLayout(cards, from, to);
        const taken = cards.filter(c => c.shown).map(c => first.get(c.key));
        for (const c of arriving) {
            const want = first.get(c.key), spot = findRoom(want, taken, to) ?? want;
            taken.push(spot);
            c.rect = file(c.key, spot);
            c.shown = true;
        }
        for (const p of stragglers) {
            const r = p.getBoundingClientRect();
            if (r.width < 1 || r.height < 1) continue;
            const want = { x: r.left, y: r.top, w: r.width, h: r.height };
            const spot = findRoom(want, taken, to) ?? want;
            taken.push(spot);
            p.classList.add('hud-free');
            cards.push({ key: p.dataset.hudKey, p, rect: file(p.dataset.hudKey, spot), shown: true, seen: false });
        }
    }
    shown = new Set(cards.filter(c => c.shown).map(c => c.key));

    const plan = planLayout(cards, from, to);
    for (const c of cards) writeRect(c.p, plan.get(c.key));
    if (arriving.length || stragglers.length) save();
}

/**
 * File what is on screen as the owner's arrangement for this window size —
 * the one already filed for it if there is one within SAME_SIZE, a new one if
 * not. The other sizes are untouched: tidying a half-screen window must not
 * cost the owner their maximised one.
 */
function commit() {
    const W = innerWidth, H = innerHeight;
    let l = nearest(W, H);
    if (!l || Math.abs(Math.log(W / l.w)) > SAME_SIZE || Math.abs(Math.log(H / l.h)) > SAME_SIZE) {
        l = { panels: {} };
        layouts.push(l);
    }
    Object.assign(l, { w: W, h: H, g: lane(), used: tick(), seen: [] });
    for (const p of panels) {
        if (!p.dataset.hudKey || !p.classList.contains('hud-free')) continue;
        const r = readRect(p);
        if (r.w >= 1 && r.h >= 1) l.panels[p.dataset.hudKey] = r;
        if (isShown(p)) l.seen.push(p.dataset.hudKey);
    }
    while (layouts.length > MAX_LAYOUTS) {
        const stale = layouts.reduce((a, b) => (b.used < a.used ? b : a));
        layouts.splice(layouts.indexOf(stale), 1);
    }
    save();
}

// ── Persistence ──────────────────────────────────────────────────

function save() {
    if (!placed || !layouts.length) return;
    // The top-level w/h/panels are the arrangement in use, in exactly the shape
    // the first version of this file wrote — an older build reads those and
    // never looks at `layouts`.
    const recent = layouts.reduce((a, b) => (b.used > a.used ? b : a));
    const data = { v: 2, w: recent.w, h: recent.h, panels: recent.panels, layouts };
    try { localStorage.setItem(KEY, JSON.stringify(data)); } catch { /* private mode */ }
}

/** Whatever is stored, as a clean list of arrangements. Anything malformed is
 *  dropped rather than trusted: a NaN in one rect would otherwise propagate
 *  through the planner into every card it shares a lane with. */
function readLayouts(data) {
    const rectOk = r => r && [r.x, r.y, r.w, r.h].every(Number.isFinite) && r.w >= 1 && r.h >= 1;
    const gutter = g => g && [g.l, g.r, g.t, g.b].every(Number.isFinite) ? { l: g.l, r: g.r, t: g.t, b: g.b } : null;
    const one = l => {
        const w = Number(l?.w), h = Number(l?.h);
        if (!(w > 0 && h > 0 && Number.isFinite(w) && Number.isFinite(h))) return null;
        if (!l.panels || typeof l.panels !== 'object') return null;
        const kept = {};
        for (const [k, r] of Object.entries(l.panels))
            if (AREAS.includes(k) && rectOk(r)) kept[k] = { x: r.x, y: r.y, w: r.w, h: r.h };
        if (!Object.keys(kept).length) return null;
        return {
            w, h, g: gutter(l.g), used: Number.isFinite(l.used) ? l.used : 0, panels: kept,
            ...(Array.isArray(l.seen) ? { seen: l.seen.filter(k => AREAS.includes(k)) } : {}),
        };
    };
    // No `layouts`: written by the first version of this file — one
    // arrangement, filed under the window it was last saved at.
    const list = Array.isArray(data?.layouts) ? data.layouts
        : data?.panels ? [{ w: data.w || innerWidth, h: data.h || innerHeight, panels: data.panels }]
        : [];
    return list.map(one).filter(Boolean).slice(-MAX_LAYOUTS);
}

/**
 * Apply the stored arrangements. Nothing here MEASURES a panel — a saved rect
 * is absolute, so this runs correctly the instant the DOM exists, before web
 * fonts land and before the first frame is composited. That matters: the
 * obvious implementation defers to requestAnimationFrame so the grid can
 * settle first, and rAF does not fire in a window that is not compositing —
 * a HUD that boots behind another view or on a minimised window would sit in
 * its default arrangement until something happened to show it.
 *
 * A panel with NO stored rect is deliberately left on the grid rather than
 * pinned at whatever it measures right now: mid-boot that measurement is
 * taken against fallback metrics, and freezing it would make a one-off font
 * swap permanent. relayout() gives it free room once the page has settled.
 */
function restore() {
    let data = null;
    try { data = JSON.parse(localStorage.getItem(KEY) || 'null'); } catch { /* corrupt */ }
    layouts = readLayouts(data);
    if (!layouts.length) return;
    clock = Math.max(0, ...layouts.map(l => l.used));

    markPlaced();
    for (const p of panels)
        if (layouts.some(l => l.panels[p.dataset.hudKey])) p.classList.add('hud-free');
    relayout();
}

// ── The way back ─────────────────────────────────────────────────

function wireReset() {
    resetBtn = document.getElementById('hud-layout-reset');
    resetBtn?.addEventListener('click', onReset);
    syncResetBtn();
}

/* Reset throws away every arrangement — one per window size now, not one — so
   it asks first: the first click arms it and says what it is about to do, a
   second click within a few seconds does it. The button answering in place is
   the confirmation; a modal dialog over a live render would be a heavier way
   to ask a one-word question. */
let armed = null;
function onReset() {
    if (armed) { disarm(); resetHudLayout(); return; }
    resetBtn.dataset.label ??= resetBtn.textContent;
    resetBtn.textContent = layouts.length > 1
        ? `Click again to reset all ${layouts.length} layouts`
        : 'Click again to reset';
    resetBtn.classList.add('is-armed');
    armed = setTimeout(disarm, 4000);
}

function disarm() {
    clearTimeout(armed);
    armed = null;
    if (!resetBtn) return;
    resetBtn.classList.remove('is-armed');
    if (resetBtn.dataset.label) resetBtn.textContent = resetBtn.dataset.label;
}

/** The button only exists once there is something to undo. A permanent "Reset
 *  layout" on a HUD nobody has touched is a control that does nothing. */
function syncResetBtn() {
    if (resetBtn) resetBtn.hidden = !placed;
    document.body.classList.toggle('hud-placed', placed);
}
