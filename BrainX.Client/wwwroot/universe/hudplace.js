/* BrainX Universe HUD — where the placed cards go when the window changes shape.
 *
 * hudlayout.js owns the pointer and the storage. This file owns one question:
 * the owner arranged the cards at one window size, so where does each card sit
 * at THIS one? It is pure geometry — no DOM, no storage — which is what lets
 * the rules be exercised from node without a browser:
 *     node BrainX.Tests/hud/hudplace.check.mjs
 *
 * The rules, in the order they bind:
 *
 *  1. Every size is derived from what the owner placed, never from the last
 *     size. The first version re-anchored the CURRENT rects and saved the
 *     result, so every resize was lossy: shrink the window and the cards were
 *     clamped, grow it back and the clamped sizes were the new truth.
 *     Deriving from the arrangement itself is what makes maximise → restore →
 *     maximise a round trip that ends exactly where it started.
 *
 *  2. A card keeps its anchor. Against an edge it keeps its distance from that
 *     edge; flush with both gutters it stretches with the window; floating in
 *     between it keeps its place as a fraction of the window.
 *
 *  3. Cards the owner kept apart stay apart. When a smaller window pushes two
 *     together they keep their order — the left one stays left — and the lane
 *     they share is divided in proportion to the size each was given, down to
 *     a floor. Only pairs that actually collide are constrained, so no card is
 *     ever shrunk for a neighbour it could not have touched. Cards the owner
 *     stacked on each other by hand are left stacked.
 *
 *  4. A card stays inside the gutters unless the owner put it in them, and a
 *     card the owner parked half off-screen stays parked, keeping only enough
 *     of itself on screen to be grabbed again.
 */

export const MIN_W = 148;   // narrower than this and the title chip itself wraps
export const MIN_H = 46;    // one title chip and nothing else — a deliberate "collapsed"
const KEEP_X = 90;          // what a card hanging off an edge must leave on screen…
const KEEP_Y = 34;          // …to be grabbed again

const FLUSH = 8;   // px from a gutter that still counts as sitting against it (the drag snap)
const TOUCH = 1;   // px of overlap that is rounding, not stacking

/** Do two rects overlap by more than rounding? Cards snapped flush against
 *  each other share an edge, and a shared edge is not a collision. */
export function overlaps(a, b) {
    return Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x) > TOUCH
        && Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y) > TOUCH;
}

/**
 * A card dragged off an edge is not hidden, it is LOST: there is no window
 * list to get it back from, and the only recovery would be Reset layout, which
 * throws away every other placement too. So it always keeps a graspable
 * amount of itself inside the window — and never goes above the top, where
 * the title chip it is dragged by would be out of reach.
 */
export function reachable(r, to) {
    const kx = Math.min(r.w, KEEP_X), ky = Math.min(r.h, KEEP_Y);
    return {
        ...r,
        x: Math.min(Math.max(r.x, kx - r.w), to.w - kx),
        y: Math.min(Math.max(r.y, 0), to.h - ky),
    };
}

/**
 * Place one saved arrangement in the current window.
 *
 * @param cards  [{ key, rect: {x,y,w,h}, shown, seen }] with `rect` in the
 *               `from` window. A card that is not shown is placed by its
 *               anchors alone and takes no room from the others — hiding a
 *               card gives its neighbours their room back. `seen: false`
 *               marks a card the owner never saw in this arrangement: if it
 *               overlaps another, nobody stacked them on purpose, so they are
 *               parted like any other pair instead of being left stacked.
 * @param from   { w, h, g: {l,r,t,b} } — the window the rects were placed in,
 *               and its gutters
 * @param to     { w, h, g: {l,r,t,b} } — the window to place them in now
 * @returns Map key → {x,y,w,h} in whole pixels, for every card passed in
 */
export function planLayout(cards, from, to) {
    const out = new Map();
    const live = [];
    for (const c of cards) {
        const { ax, ay } = axes(c.rect, from, to);
        if (ax.parked || ay.parked) out.set(c.key, whole(reachable(anchored(ax, ay), to)));
        else if (!c.shown) out.set(c.key, whole(alone(ax, ay)));
        else live.push({ key: c.key, ax, ay, i: live.length, seen: c.seen !== false });
    }

    const gap = to.g.l;
    const pairs = [];
    for (let i = 0; i < live.length; i++)
        for (let j = i + 1; j < live.length; j++) {
            const pr = apart(live[i], live[j], from, gap);
            if (pr) pairs.push(pr);
        }

    const solve = () => {
        const xs = settle(live.map(n => n.ax), pairs.filter(p => p.on && p.k === 'ax'));
        const ys = settle(live.map(n => n.ay), pairs.filter(p => p.on && p.k === 'ay'));
        return live.map(n => ({ x: xs.p[n.i], y: ys.p[n.i], w: xs.s[n.i], h: ys.s[n.i] }));
    };
    // Constrain only the pairs that actually collide, and look again after
    // every settle — parting one pair can push a card into a third. Each round
    // switches at least one pair on and none off, so this ends.
    let rects = solve();
    for (let more = true; more;) {
        more = false;
        for (const pr of pairs)
            if (!pr.on && overlaps(rects[pr.a], rects[pr.b])) pr.on = more = true;
        if (more) rects = solve();
    }
    for (const n of live) out.set(n.key, whole(rects[n.i]));
    return out;
}

/**
 * One rect carried from one window to another by its anchors alone, with no
 * neighbours considered. For filing a card into an arrangement saved at a
 * different size: the full plan runs again on the way back out, so all this
 * has to get right is where the card hangs.
 */
export function mapRect(r, from, to) {
    const { ax, ay } = axes(r, from, to);
    return whole(ax.parked || ay.parked ? reachable(anchored(ax, ay), to) : alone(ax, ay));
}

/**
 * A free spot for a card joining a placed layout — one the grid was still
 * holding when the owner arranged the rest, or one coming back from hidden
 * onto cards that were arranged while it was away.
 *
 * The candidates are the lines a person would reach for: flush against the
 * gutters, or one gap clear of another card's edge. Of those, the free spot
 * nearest to where the card wanted to be. If nothing is free at full size it
 * tries smaller. If nothing is free at all it answers null, and the caller
 * keeps the card where it was — planLayout will part it from whatever it is
 * sitting on, since the owner never saw the two stacked.
 */
export function findRoom(want, others, to) {
    const gap = to.g.l;
    const lx = to.g.l, hx = to.w - to.g.r, ly = to.g.t, hy = to.h - to.g.b;
    for (const f of [1, 0.75, 0.5]) {
        const w = Math.max(0, Math.min(Math.max(want.w * f, Math.min(MIN_W, want.w)), hx - lx));
        const h = Math.max(0, Math.min(Math.max(want.h * f, Math.min(MIN_H, want.h)), hy - ly));
        const xs = [want.x, lx, hx - w], ys = [want.y, ly, hy - h];
        for (const o of others) {
            xs.push(o.x + o.w + gap, o.x - gap - w);
            ys.push(o.y + o.h + gap, o.y - gap - h);
        }
        let best = null, bestD = Infinity;
        for (const x of xs) for (const y of ys) {
            if (x < lx - 0.5 || x + w > hx + 0.5 || y < ly - 0.5 || y + h > hy + 0.5) continue;
            const r = { x, y, w, h };
            if (others.some(o => overlaps(o, r))) continue;
            const d = (x - want.x) ** 2 + (y - want.y) ** 2;
            if (d < bestD) { bestD = d; best = r; }
        }
        if (best) return whole(best);
    }
    return null;
}

// ── One axis of one card ─────────────────────────────────────────

function axes(r, from, to) {
    return {
        ax: axis(r.x, r.w, from.w, to.w, [from.g.l, from.g.r], [to.g.l, to.g.r], MIN_W),
        ay: axis(r.y, r.h, from.h, to.h, [from.g.t, from.g.b], [to.g.t, to.g.b], MIN_H),
    };
}

/**
 * How a card hangs along one axis of the window it was placed in, and what
 * that means in the window it is going to. `gF`/`gT` are the [head, tail]
 * gutters of each window: the gutter is a clamp() of the window width, so it
 * is not the same number in both.
 *
 * The edge zone scales with the window so the same layout behaves the same way
 * on a laptop and on a 4K panel, with a floor for genuinely small windows.
 * Deciding by "which half is its centre in" looked equivalent and was not: the
 * top-centre card sat one pixel right of centre, counted as right-anchored,
 * and a 1280 → 900 resize slid it 380 px to the left.
 */
function axis(pos, size, F, T, gF, gT, floor) {
    const head = pos, tail = F - pos - size;
    const parked = head < -0.5 || tail < -0.5;
    // Measured from the gutter line, so a card that sat against the gutter
    // still does after the gutter moves. A card the owner put IN the gutter
    // keeps its raw distance from the window edge instead.
    const inH = head >= gF[0] - 0.5 ? head - gF[0] + gT[0] : head;
    const inT = tail >= gF[1] - 0.5 ? tail - gF[1] + gT[1] : tail;
    const min = Math.min(size, floor);
    const zone = Math.max(120, F * 0.15);
    let kind, want = size;
    if (!parked && head <= gF[0] + FLUSH && tail <= gF[1] + FLUSH) {
        kind = 'both';                       // a bar the owner stretched gutter to gutter
        want = Math.max(min, T - inH - inT);
    }
    else if (head <= zone && head <= tail) kind = 'head';
    else if (tail <= zone && tail < head) kind = 'tail';
    else kind = 'mid';
    const centre = (pos + size / 2) / F * T;
    return {
        kind, want, min, parked, T,
        // The lane: gutter to gutter, or the window edge on a side where the
        // owner had put the card in the gutter.
        lo: head < gF[0] - 0.5 ? 0 : gT[0],
        hi: tail < gF[1] - 0.5 ? T : T - gT[1],
        // Where the card starts at a given size: the anchored edge stays put,
        // so a shrunk right-hand card gives up width on its inner side.
        at: s => kind === 'tail' ? T - inT - s : kind === 'mid' ? centre - s / 2 : inH,
        d0: pos, d1: pos + size,             // the placed span, for order and adjacency
    };
}

// Where a card goes on its own: anchored, and no bigger than its lane.
function alone(ax, ay) {
    const w = Math.max(0, Math.min(ax.want, ax.hi - ax.lo));
    const h = Math.max(0, Math.min(ay.want, ay.hi - ay.lo));
    return {
        x: Math.min(Math.max(ax.at(w), ax.lo), ax.hi - w),
        y: Math.min(Math.max(ay.at(h), ay.lo), ay.hi - h),
        w, h,
    };
}

// Anchors only, lane ignored — for a card parked half off-screen on purpose.
const anchored = (ax, ay) => ({ x: ax.at(ax.want), y: ay.at(ay.want), w: ax.want, h: ay.want });

// Round the EDGES, not the sizes: two cards whose right edges agree before
// rounding must still agree after it, and round(x) + round(w) does not promise
// that.
function whole(r) {
    const x = Math.round(r.x), y = Math.round(r.y);
    return { x, y, w: Math.round(r.x + r.w) - x, h: Math.round(r.y + r.h) - y };
}

/**
 * Were two cards apart as the owner placed them — and if so, along which axis
 * do they part when a smaller window pushes them together?
 *
 * Side by side when they share a band of rows, one above the other when they
 * share a column. Diagonal neighbours part along whichever gap was wider as a
 * share of the window, so a tall window does not bias every choice to one
 * axis. The gap they keep is the one they had, up to the HUD's own gutter.
 *
 * Overlapping cards were stacked by hand and stay stacked — unless one of
 * them was not on screen when the other was put there. Those part along the
 * axis their centres are furthest apart on, a full gutter between them.
 * Either way the pair is ordered by where each STARTS on that axis, which is
 * the order settle() places in; ordering by centres would let a wide card
 * that starts first be placed second.
 */
function apart(a, b, from, gap) {
    const ox = Math.min(a.ax.d1, b.ax.d1) - Math.max(a.ax.d0, b.ax.d0);
    const oy = Math.min(a.ay.d1, b.ay.d1) - Math.max(a.ay.d0, b.ay.d0);
    if (ox > TOUCH && oy > TOUCH) {
        if (a.seen && b.seen) return null;             // stacked by hand: leave them stacked
        const cx = Math.abs(a.ax.d0 + a.ax.d1 - b.ax.d0 - b.ax.d1) / from.w;
        const cy = Math.abs(a.ay.d0 + a.ay.d1 - b.ay.d0 - b.ay.d1) / from.h;
        const k = cx >= cy ? 'ax' : 'ay';
        const [first, second] = a[k].d0 <= b[k].d0 ? [a, b] : [b, a];
        return { k, a: first.i, b: second.i, gap, on: false };
    }
    const onX = ox <= TOUCH && (oy > TOUCH || -ox / from.w >= -oy / from.h);
    const k = onX ? 'ax' : 'ay';
    const [first, second] = a[k].d0 <= b[k].d0 ? [a, b] : [b, a];
    return { k, a: first.i, b: second.i, gap: Math.min(Math.max(0, -(onX ? ox : oy)), gap), on: false };
}

/**
 * One axis: sizes first, then positions.
 *
 * Every chain of cards that must not overlap has to fit the lane between its
 * two ends. A chain that does not is shrunk — each card in proportion to what
 * it has above its floor, so a wide card gives more than a narrow one and none
 * goes below MIN. A card on several chains takes the smallest size any of them
 * allows it, which is what guarantees every chain fits at once.
 *
 * Cards that shared a span as placed — a column of equal widths, a row of
 * equal heights — then share one size again: each chain can hand its members
 * a slightly different one, and a column whose inner edge wanders by four
 * pixels reads as broken long before any overlap would. That can only shrink
 * a card, which leaves room its neighbours were squeezed out of for nothing,
 * so the chains are fitted once more with the columns held at their shared
 * size — and the neighbours take that room back.
 *
 * Then the cards are placed in order along the axis, each at its anchored spot
 * if it can have it: no earlier than the cards before it allow, no later than
 * the room the cards after it still need. So of two colliding cards the first
 * keeps its place and the second moves on — unless the second is pinned to
 * the far edge, in which case the room it needs pushes the first one back.
 * Sizes are final before anything is placed: shrinking a card that has
 * already pushed its neighbours along would leave them stranded where it
 * used to end.
 */
function settle(axes, rels) {
    const n = axes.length;
    const succ = axes.map(() => []), pred = axes.map(() => []);
    for (const r of rels) { succ[r.a].push(r); pred[r.b].push(r); }

    // Each chain from a card with nothing before it to one with nothing after.
    const fit = (base, floor) => {
        const s = base.slice();
        const walk = (i, path, gaps) => {
            path.push(i);
            if (!succ[i].length) {
                const room = axes[path[path.length - 1]].hi - axes[path[0]].lo - gaps;
                let total = 0, slack = 0;
                for (const k of path) { total += base[k]; slack += base[k] - floor[k]; }
                if (total > room) {
                    const f = slack > 0 ? Math.max(0, 1 - (total - room) / slack) : 0;
                    for (const k of path) s[k] = Math.min(s[k], floor[k] + (base[k] - floor[k]) * f);
                }
            }
            for (const r of succ[i]) walk(r.b, path, gaps + r.gap);
            path.pop();
        };
        for (let i = 0; i < n; i++) if (!pred[i].length && succ[i].length) walk(i, [], 0);
        return s;
    };

    const lane = axes.map(a => Math.max(0, Math.min(a.want, a.hi - a.lo)));
    const floor = axes.map((a, i) => Math.min(a.min, lane[i]));
    let s = fit(lane, floor);

    const same = (a, b) => Math.abs(a.d0 - b.d0) <= TOUCH && Math.abs(a.d1 - b.d1) <= TOUCH;
    const column = axes.map(a => axes.reduce((c, b, j) => (same(a, b) ? [...c, j] : c), []));
    if (column.some(c => c.length > 1)) {
        const shared = column.map(c => Math.min(...c.map(j => s[j])));
        const held = column.map(c => c.length > 1);
        s = fit(lane.map((v, i) => (held[i] ? shared[i] : v)), floor.map((v, i) => (held[i] ? shared[i] : v)));
    }

    // Every relation runs from the card placed earlier on this axis to the
    // later one, so placement order is a valid order for both passes.
    const order = [...axes.keys()].sort((i, j) => axes[i].d0 - axes[j].d0 || i - j);
    const late = new Array(n);
    for (const i of [...order].reverse()) {
        let h = axes[i].hi - s[i];
        for (const r of succ[i]) h = Math.min(h, late[r.b] - r.gap - s[i]);
        late[i] = h;
    }
    const p = new Array(n);
    for (const i of order) {
        let lo = axes[i].lo;
        for (const r of pred[i]) lo = Math.max(lo, p[r.a] + s[r.a] + r.gap);
        // lo > late only when even the floors cannot fit: keep the order and
        // let the far bound give — but never leave the window.
        const at = lo > late[i] ? lo : Math.min(Math.max(axes[i].at(s[i]), lo), late[i]);
        p[i] = Math.max(0, Math.min(at, axes[i].T - s[i]));
    }
    return { p, s };
}
