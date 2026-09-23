// Rules for where placed HUD cards go when the window changes shape.
// Pure geometry, so it runs without a browser:
//     node BrainX.Tests/hud/hudplace.check.mjs
// Exit 0 = every check passed. Same contract as the C# harness.

import { planLayout, mapRect, findRoom, overlaps, reachable, MIN_W, MIN_H }
    from '../../BrainX.Client/wwwroot/universe/hudplace.js';

let passed = 0, failed = 0;
function check(name, ok, detail) {
    if (ok) { passed++; return; }
    failed++;
    console.log(`FAIL ${name}${detail !== undefined ? `\n     ${typeof detail === 'string' ? detail : JSON.stringify(detail)}` : ''}`);
}

// The HUD's own gutters: --hud-gap is clamp(10px, 1.4vw, 22px), and the
// bottom one carries the 26px controls strip under it.
function frame(w, h) {
    const gap = Math.min(22, Math.max(10, w * 0.014));
    return { w, h, g: { l: gap, r: gap, t: gap, b: gap + 26 } };
}

// The default grid as the owner froze it on a maximised 1920×1040 window.
const BIG = frame(1920, 1040);
const DESIGN = {
    tl: { x: 22, y: 22, w: 380, h: 260 },
    tc: { x: 710, y: 22, w: 500, h: 140 },
    tr: { x: 1606, y: 22, w: 292, h: 380 },
    ml: { x: 22, y: 304, w: 380, h: 300 },
    mr: { x: 1606, y: 424, w: 292, h: 280 },
    bl: { x: 22, y: 626, w: 380, h: 366 },
    bc: { x: 730, y: 742, w: 460, h: 250 },
    br: { x: 1606, y: 726, w: 292, h: 266 },
};
const cardsOf = (design, hidden = []) =>
    Object.entries(design).map(([key, rect]) => ({ key, rect, shown: !hidden.includes(key) }));

function laneOk(r, f) {
    return r.x >= f.g.l - 1 && r.y >= f.g.t - 1
        && r.x + r.w <= f.w - f.g.r + 1 && r.y + r.h <= f.h - f.g.b + 1;
}
function collisions(plan, keys) {
    const hits = [];
    for (let i = 0; i < keys.length; i++)
        for (let j = i + 1; j < keys.length; j++)
            if (overlaps(plan.get(keys[i]), plan.get(keys[j]))) hits.push(`${keys[i]}×${keys[j]}`);
    return hits;
}

// ── 1. At the size it was placed, an arrangement is exactly itself ──
{
    const plan = planLayout(cardsOf(DESIGN), BIG, BIG);
    for (const [k, r] of Object.entries(DESIGN))
        check(`identity: ${k} is where the owner put it`, JSON.stringify(plan.get(k)) === JSON.stringify(r),
              { want: r, got: plan.get(k) });
}

// ── 2. Smaller windows: nothing collides, nothing leaves the lanes ──
for (const [w, h] of [[1600, 900], [1280, 720], [1100, 640], [1000, 600], [900, 560]]) {
    const to = frame(w, h);
    const plan = planLayout(cardsOf(DESIGN), BIG, to);
    const keys = Object.keys(DESIGN);
    check(`${w}×${h}: no two cards overlap`, collisions(plan, keys).length === 0, collisions(plan, keys).join(', '));
    for (const k of keys) check(`${w}×${h}: ${k} inside the gutters`, laneOk(plan.get(k), to), plan.get(k));
    for (const k of keys) {
        const r = plan.get(k);
        check(`${w}×${h}: ${k} keeps its floor`, r.w >= Math.min(MIN_W, DESIGN[k].w) - 1 && r.h >= Math.min(MIN_H, DESIGN[k].h) - 1, r);
    }
    // Order along each axis survives the squeeze.
    const p = k => plan.get(k);
    check(`${w}×${h}: top row keeps its order`, p('tl').x < p('tc').x && p('tc').x < p('tr').x);
    check(`${w}×${h}: left column keeps its order`, p('tl').y < p('ml').y && p('ml').y < p('bl').y);
    check(`${w}×${h}: right column keeps its order`, p('tr').y < p('mr').y && p('mr').y < p('br').y);
    // Columns placed as one straight edge stay one straight edge.
    check(`${w}×${h}: the left column's inner edge is still straight`,
          new Set(['tl', 'ml', 'bl'].map(k => p(k).x + p(k).w)).size === 1, ['tl', 'ml', 'bl'].map(k => p(k)));
    check(`${w}×${h}: the right column's inner edge is still straight`,
          new Set(['tr', 'mr', 'br'].map(k => p(k).x)).size === 1, ['tr', 'mr', 'br'].map(k => p(k)));
    // Corners stay in their corners.
    const g = to.g;
    check(`${w}×${h}: tl stays against the top-left gutters`, Math.abs(p('tl').x - g.l) <= 1 && Math.abs(p('tl').y - g.t) <= 1, p('tl'));
    check(`${w}×${h}: tr stays against the top-right gutters`,
          Math.abs(p('tr').x + p('tr').w - (w - g.r)) <= 1 && Math.abs(p('tr').y - g.t) <= 1, p('tr'));
    check(`${w}×${h}: br stays against the bottom-right gutters`,
          Math.abs(p('br').x + p('br').w - (w - g.r)) <= 1 && Math.abs(p('br').y + p('br').h - (h - g.b)) <= 1, p('br'));
}

// ── 3. A little smaller changes a little ──────────────────────────
{
    // 1920 → 1850: the design still fits, so no card may change size.
    const to = frame(1850, 1000);
    const plan = planLayout(cardsOf(DESIGN), BIG, to);
    const resized = Object.keys(DESIGN).filter(k => {
        const a = plan.get(k), d = DESIGN[k];
        return a.w !== d.w || (a.h !== d.h && d.h <= to.h - to.g.t - to.g.b);
    });
    // The columns lose 40px of height, which the stacked cards must absorb —
    // but no card may lose WIDTH when the width still fits.
    const narrowed = Object.keys(DESIGN).filter(k => plan.get(k).w !== DESIGN[k].w);
    check('1850×1000: no card is narrowed when the widths still fit', narrowed.length === 0, narrowed.join(', '));
    check('1850×1000: planning ran', resized !== null);
}

// ── 4. Bigger windows: sizes stay, anchors spread ─────────────────
{
    const to = frame(2560, 1400);
    const plan = planLayout(cardsOf(DESIGN), BIG, to);
    for (const k of Object.keys(DESIGN))
        check(`2560×1400: ${k} keeps the size the owner gave it`,
              plan.get(k).w === DESIGN[k].w && plan.get(k).h === DESIGN[k].h, plan.get(k));
    const tc = plan.get('tc');
    check('2560×1400: the centred card stays centred', Math.abs(tc.x + tc.w / 2 - 1280) <= 1, tc);
    check('2560×1400: tr follows the right edge', Math.abs(plan.get('tr').x + 292 - (2560 - 22)) <= 1, plan.get('tr'));
}

// ── 5. A bar stretched gutter to gutter stretches with the window ──
{
    const bar = { x: 22, y: 900, w: 1920 - 44, h: 92 };
    const to = frame(1280, 720);
    const r = planLayout([{ key: 'bar', rect: bar, shown: true }], BIG, to).get('bar');
    check('stretched bar: spans the new window gutter to gutter',
          Math.abs(r.x - to.g.l) <= 1 && Math.abs(r.x + r.w - (1280 - to.g.r)) <= 1, r);
    check('stretched bar: keeps its height', r.h === 92, r);
    check('stretched bar: stays on the bottom gutter', Math.abs(r.y + r.h - (720 - to.g.b)) <= 1, r);
}

// ── 6. Cards stacked by hand stay stacked ─────────────────────────
{
    const a = { x: 400, y: 300, w: 420, h: 300 }, b = { x: 600, y: 420, w: 420, h: 300 };
    const plan = planLayout([{ key: 'a', rect: a, shown: true }, { key: 'b', rect: b, shown: true }], BIG, frame(1280, 720));
    check('stacked by hand: still stacked after a resize', overlaps(plan.get('a'), plan.get('b')), [plan.get('a'), plan.get('b')]);
}

// ── 7. A card parked half off-screen stays parked, and reachable ───
{
    const parked = { x: -250, y: 300, w: 340, h: 200 };
    const to = frame(1280, 720);
    const r = planLayout([{ key: 'p', rect: parked, shown: true }], BIG, to).get('p');
    check('parked: still hanging off the left edge', r.x < 0, r);
    check('parked: keeps a grabbable 90px on screen', r.x + r.w >= 90 - 1, r);
    const off = reachable({ x: 5000, y: -40, w: 300, h: 200 }, to);
    check('reachable: a card thrown off the right comes back to 90px', off.x === 1280 - 90, off);
    check('reachable: never above the top', off.y === 0, off);
}

// ── 8. A hidden card gives its neighbours their room back ─────────
{
    const to = frame(1100, 600);
    const withMl = planLayout(cardsOf(DESIGN), BIG, to);
    const without = planLayout(cardsOf(DESIGN, ['ml']), BIG, to);
    check('hidden ml: bl gets taller when ml is not taking room',
          without.get('bl').h > withMl.get('bl').h, { with: withMl.get('bl'), without: without.get('bl') });
    check('hidden ml: still gets a rect for when it comes back', !!without.get('ml') && without.get('ml').w > 0);
}

// ── 8b. A pushed card stops one gap clear, not where a neighbour used to end ──
{
    // 800 wide: the breakpoint has hidden ml/mr/br. bc wants the centre, bl
    // pushes it right — and bl is narrowed to match tl and ml above it. bc
    // must sit against bl's FINAL edge; the first cut placed it against the
    // edge bl had before the column was straightened, 110 px further out.
    const to = frame(800, 600);
    const plan = planLayout(cardsOf(DESIGN, ['ml', 'mr', 'br']), BIG, to);
    const bl = plan.get('bl'), bc = plan.get('bc');
    check('800×600: bc sits one gap clear of bl', Math.abs(bc.x - (bl.x + bl.w + to.g.l)) <= 1, { bl, bc, gap: to.g.l });
    check('800×600: nothing overlaps with three cards hidden',
          collisions(plan, ['tl', 'tc', 'tr', 'bl', 'bc']).length === 0, collisions(plan, ['tl', 'tc', 'tr', 'bl', 'bc']).join(', '));
}

// ── 9. Moving a rect between windows by anchors ───────────────────
{
    const small = frame(1280, 720);
    const tr = mapRect(DESIGN.tr, BIG, small);
    check('mapRect: tr keeps its right-gutter distance', Math.abs(tr.x + tr.w - (1280 - small.g.r)) <= 1, tr);
    const back = mapRect(tr, small, BIG);
    check('mapRect: and maps back to where it came from', JSON.stringify(back) === JSON.stringify(DESIGN.tr), back);
}

// ── 10. A card joining the layout finds free room ─────────────────
{
    const to = BIG;
    const others = Object.entries(DESIGN).filter(([k]) => k !== 'ml').map(([, r]) => r);
    // It wants to land squarely on top of tl.
    const spot = findRoom({ x: 30, y: 40, w: 380, h: 260 }, others, to);
    check('findRoom: the spot overlaps nothing', !others.some(o => overlaps(o, spot)), spot);
    check('findRoom: the spot is inside the gutters', laneOk(spot, to), spot);
    check('findRoom: it stays near where it wanted to be (left column)', spot.x < 500, spot);
    // A window with no room at all says so, and the caller keeps its spot.
    const full = [{ x: 0, y: 0, w: 1920, h: 1040 }];
    check('findRoom: nowhere free → null', findRoom({ x: 100, y: 100, w: 300, h: 200 }, full, to) === null);
}

// ── 10b. Stacked by hand vs. stacked because one was never seen ────
{
    const a = { x: 400, y: 300, w: 420, h: 300 }, b = { x: 600, y: 420, w: 420, h: 300 };
    const small = frame(1280, 720);
    const seenBoth = planLayout([{ key: 'a', rect: a, shown: true }, { key: 'b', rect: b, shown: true, seen: true }], BIG, small);
    check('seen overlap: left stacked', overlaps(seenBoth.get('a'), seenBoth.get('b')));
    const oneUnseen = planLayout([{ key: 'a', rect: a, shown: true }, { key: 'b', rect: b, shown: true, seen: false }], BIG, small);
    check('unseen overlap: parted', !overlaps(oneUnseen.get('a'), oneUnseen.get('b')), [oneUnseen.get('a'), oneUnseen.get('b')]);
    check('unseen overlap: the one that started first stays first',
          oneUnseen.get('a').x < oneUnseen.get('b').x, [oneUnseen.get('a'), oneUnseen.get('b')]);
    // The same at the size it was placed: a card that was hidden when the
    // others were arranged over its spot comes back beside them, not on them.
    const atHome = planLayout([
        { key: 'tl', rect: DESIGN.tl, shown: true },
        { key: 'ml', rect: DESIGN.ml, shown: true },
        { key: 'x', rect: { x: 100, y: 200, w: 380, h: 200 }, shown: true, seen: false },
    ], BIG, BIG);
    check('unseen overlap at home size: nothing left stacked', collisions(atHome, ['tl', 'ml', 'x']).length === 0,
          collisions(atHome, ['tl', 'ml', 'x']).join(', '));
}

// ── 11. Random arrangements at random sizes ───────────────────────
{
    // Deterministic, so a failure reproduces.
    let seed = 20260923;
    const rnd = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648);
    const between = (a, b) => a + rnd() * (b - a);

    let runs = 0, clean = 0, outOfLane = 0, underFloor = 0;
    const firstBad = [];
    for (let t = 0; t < 400; t++) {
        const from = frame(Math.round(between(1200, 2560)), Math.round(between(700, 1440)));
        // Cards in a 3×3 grid of cells, each a random box inside its cell —
        // apart by construction, like any arrangement a person makes.
        const cw = (from.w - from.g.l - from.g.r) / 3, ch = (from.h - from.g.t - from.g.b) / 3;
        const design = {};
        let n = 0;
        for (let cx = 0; cx < 3; cx++) for (let cy = 0; cy < 3; cy++) {
            if (rnd() < 0.2 || (cx === 1 && cy === 1)) continue;
            const w = Math.round(between(Math.min(MIN_W + 10, cw - 12), cw - 12));
            const h = Math.round(between(Math.min(MIN_H + 10, ch - 12), ch - 12));
            const x = Math.round(from.g.l + cx * cw + between(0, cw - 12 - w) + 6);
            const y = Math.round(from.g.t + cy * ch + between(0, ch - 12 - h) + 6);
            design['c' + n++] = { x, y, w, h };
        }
        // Down to 55% of the design size — well below where the owner placed it,
        // not so small that three floors cannot share one lane.
        const to = frame(Math.round(from.w * between(0.55, 1.4)), Math.round(from.h * between(0.6, 1.4)));
        const plan = planLayout(cardsOf(design), from, to);
        const keys = Object.keys(design);
        runs++;
        const hits = collisions(plan, keys);
        if (!hits.length) clean++;
        else if (firstBad.length < 3) firstBad.push({ from: [from.w, from.h], to: [to.w, to.h], hits, design });
        for (const k of keys) {
            const r = plan.get(k);
            if (!laneOk(r, to)) outOfLane++;
            if (r.w < Math.min(MIN_W, design[k].w) - 1 || r.h < Math.min(MIN_H, design[k].h) - 1) underFloor++;
        }
    }
    check(`random: no collisions in ${runs} arrangements (clean ${clean})`, clean === runs, firstBad);
    check('random: every card inside the gutters', outOfLane === 0, `${outOfLane} card(s) out of lane`);
    check('random: no card below its floor', underFloor === 0, `${underFloor} card(s) under floor`);

    // Same again with one or two cards the owner never saw dropped anywhere —
    // usually straight onto another card. They must come out parted too.
    let runs2 = 0, clean2 = 0;
    const bad2 = [];
    for (let t = 0; t < 400; t++) {
        const from = frame(Math.round(between(1300, 2560)), Math.round(between(760, 1440)));
        const cw = (from.w - from.g.l - from.g.r) / 3, ch = (from.h - from.g.t - from.g.b) / 3;
        const cards = [];
        let n = 0;
        for (let cx = 0; cx < 3; cx++) for (let cy = 0; cy < 3; cy++) {
            if (rnd() < 0.35 || (cx === 1 && cy === 1)) continue;
            const w = Math.round(between(MIN_W + 10, Math.max(MIN_W + 11, cw * 0.8)));
            const h = Math.round(between(MIN_H + 10, Math.max(MIN_H + 11, ch * 0.8)));
            const x = Math.round(from.g.l + cx * cw + between(0, Math.max(0, cw - 12 - w)) + 6);
            const y = Math.round(from.g.t + cy * ch + between(0, Math.max(0, ch - 12 - h)) + 6);
            cards.push({ key: 'c' + n++, rect: { x, y, w, h }, shown: true });
        }
        for (let u = 0, m = 1 + (rnd() < 0.5 ? 1 : 0); u < m; u++) {
            const w = Math.round(between(MIN_W, cw * 0.7)), h = Math.round(between(MIN_H, ch * 0.7));
            cards.push({
                key: 'u' + u, shown: true, seen: false,
                rect: { x: Math.round(between(from.g.l, from.w - from.g.r - w)), y: Math.round(between(from.g.t, from.h - from.g.b - h)), w, h },
            });
        }
        const to = frame(Math.round(from.w * between(0.7, 1.3)), Math.round(from.h * between(0.75, 1.3)));
        const plan = planLayout(cards, from, to);
        runs2++;
        const keys = cards.map(c => c.key);
        const hits = collisions(plan, keys);
        if (!hits.length) clean2++;
        else if (bad2.length < 3) bad2.push({ from: [from.w, from.h], to: [to.w, to.h], hits });
    }
    check(`random with unseen cards: parted in ${runs2} arrangements (clean ${clean2})`, clean2 === runs2, bad2);
}

console.log(failed ? `\n${failed} FAILED, ${passed} passed` : `ALL CHECKS PASSED (${passed})`);
process.exit(failed ? 1 : 0);
