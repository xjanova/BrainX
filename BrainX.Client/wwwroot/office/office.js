/* The cowork room.
 *
 * Every desk in here is a file on disk. Presence decides whether anybody is
 * sitting at it, the tool-call counter decides whether they are typing, and
 * every bubble is a message that actually crossed the bus. Nothing is
 * animated because animation looks nice — if it moves, something happened.
 *
 * All art is drawn procedurally. A sprite sheet would be one more asset to
 * keep in sync with a palette that already exists in three other places, and
 * a character here is a dozen rectangles.
 */

// ── the pixel grid ──────────────────────────────────────────────────

/** Logical pixels per screen pixel. Three is the smallest that still reads as
 *  deliberate pixel art rather than a low-resolution accident. */
const SCALE = 4;
const TILE_W = 32, TILE_H = 16;     // isometric tile, 2:1 like every iso game

/* The picture is built in two passes, and that split is what separates
 * "pixel art" from "low resolution".
 *
 *   1. the SCENE, drawn at logical resolution into an offscreen canvas. Every
 *      rectangle in this file lands here, so the art stays on a hard pixel
 *      grid with no half-pixels anywhere.
 *   2. the LIGHT, drawn on the visible canvas at full device resolution after
 *      the scene has been blitted up with smoothing off.
 *
 * Doing both on one canvas forces a choice nobody should have to make: either
 * the glow is as chunky as the sprites, or the sprites are as soft as the
 * glow. Owner (2026-09-19): "คิดว่าสร้างเกม แบบ 128 bit อยู่ก็ได้" — that is
 * exactly this. Crisp sprites, cinematic lighting over the top.
 */
const cv = document.getElementById('floor');
const vctx = cv.getContext('2d', { alpha: false });   // visible, full res
const scene = document.createElement('canvas');
const ctx = scene.getContext('2d', { alpha: false }); // logical, every sprite
const overlay = document.getElementById('overlay');

/** Lamps and screens, collected while the scene draws, lit in the second pass.
 *  Gathered rather than hardcoded so a desk that moves takes its light with it. */
let LIGHTS = [];

let CW = 320, CH = 200;             // logical canvas size, recomputed on resize
let ORIGIN = { x: 160, y: 40 };     // where grid cell (0,0) lands

/** Grid cell → logical canvas point (top of the tile's diamond). */
function iso(gx, gy) {
    return {
        x: ORIGIN.x + (gx - gy) * (TILE_W / 2),
        y: ORIGIN.y + (gx + gy) * (TILE_H / 2),
    };
}

function resize() {
    const r = cv.parentElement.getBoundingClientRect();
    CW = Math.max(240, Math.round(r.width / SCALE));
    CH = Math.max(150, Math.round(r.height / SCALE));
    scene.width = CW;
    scene.height = CH;
    // The visible canvas carries the real pixels, so the lighting pass has
    // something better than the sprite grid to draw on.
    cv.width = CW * SCALE;
    cv.height = CH * SCALE;
    ctx.imageSmoothingEnabled = false;
    layoutDesks();
}

// ── palette ─────────────────────────────────────────────────────────

/** One colour per agent, stable across reloads — the same hash-to-hue idea the
 *  bus card uses, so an agent is the same colour everywhere in the app. */
const FIXED = {
    claude: '#d98b5f', codex: '#6cf0ff', cluadex: '#b98cff',
    gemini: '#7fd4ff', grok: '#9fe870', owner: '#ffb86c', broker: '#8a90b8',
};
function agentColor(name) {
    const key = (name || '').toLowerCase();
    for (const k in FIXED) if (key === k || key.startsWith(k)) return FIXED[k];
    let h = 0;
    for (let i = 0; i < key.length; i++) h = (h * 31 + key.charCodeAt(i)) | 0;
    return `hsl(${Math.abs(h) % 360}, 62%, 64%)`;
}
function label(name) {
    if (!name) return '?';
    return name.length > 14 ? name.slice(0, 13) + '…' : name;
}

// ── state ───────────────────────────────────────────────────────────

let AGENTS = [];        // [{id,label,state,lastTool,pending,spawned}]
let MESSAGES = [];      // newest last
let DECISIONS = [];
const DESKS = new Map();  // agent id → {gx,gy,seat:{x,y},screen:{x,y}}
const SEEN = new Set();   // message ids already shown as bubbles
const EMOTES_PLAYED = new Set();  // agent|atUtc, so one emote sounds once
let PRIMED = false;       // first payload is backlog: show it, don't perform it
const BUBBLES = [];       // {agent,text,color,until}
const PACKETS = [];       // {from,to,color,t0,ms}
let BOSS = null;          // {until,text} — the owner, standing in the room
/** Agents currently out of their chair: id -> {to, t0, dur}.
 *
 *  Started by REAL traffic — when one agent writes to another, the sender
 *  walks over. Nothing here is on a timer for decoration; if somebody is
 *  crossing the room it is because a message crossed the bus. */
const VISITS = new Map();
let T = 0;                // frame counter, drives every idle animation

// ── desk layout ─────────────────────────────────────────────────────

/** Desks around the edges of the room, facing in.
 *
 *  Laid out from the ROOM size rather than a fixed table, so eight agents on a
 *  small window still each get a desk instead of the last two being drawn off
 *  the edge. The order is stable (sorted by id) so a desk does not jump to the
 *  other side of the room when somebody connects. */
/** Cells between neighbouring desks, so there is floor to walk on and the
 *  name plates under each desk do not collide with the desk behind it. */
const SPACING = 2;

/** The room's own size in cells. A BOUNDED floor with two walls behind it,
 *  rather than tiles to the edge of the canvas: an unbounded floor has no
 *  back, so the desks read as furniture floating on a plain instead of people
 *  sitting in a room. Recomputed on resize to fill whatever space there is. */
let ROOM = { C: 6, R: 6 };

/** Wall height in logical pixels. Tall enough to hang something on. */
const WALL_H = 42;

function layoutRoom() {
    // Fill the HEIGHT and let the width run off the sides.
    //
    // An isometric diamond is twice as wide as it is tall, so sizing it to fit
    // both dimensions of a 4:3 canvas leaves the floor as a small lozenge in a
    // field of black — which is what the room looked like, and why it read as
    // an object rather than as a place. Sizing from the height instead puts
    // the viewer INSIDE the room: the side walls run past the frame, the way
    // they would if you were standing in one.
    const byH = Math.floor((CH - WALL_H - 18) / (TILE_H / 2));
    const S = Math.max(6, byH);
    ROOM.C = Math.max(3, Math.round(S / 2));
    ROOM.R = Math.max(3, S - ROOM.C);

    ORIGIN = {
        x: Math.round(CW / 2 - (ROOM.C - ROOM.R) * (TILE_W / 4)),
        y: Math.round((CH - ((ROOM.C + ROOM.R - 2) * (TILE_H / 2) + TILE_H)) / 2) + Math.round(WALL_H * 0.45),
    };
}

/** How many desks fit across this room. */
function deskCols(n) {
    const fits = Math.max(1, Math.floor((ROOM.C - 1) / SPACING) + 1);
    // Square-ish, so four agents are a 2x2 block filling the floor rather than
    // four desks strung out along one diagonal with the rest of the room bare.
    return Math.max(1, Math.min(fits, Math.min(4, Math.ceil(Math.sqrt(n)))));
}

function layoutDesks() {
    layoutRoom();
    DESKS.clear();
    const n = AGENTS.length || 1;
    const cols = deskCols(n);
    const rows = Math.ceil(n / cols);

    // Spread across the floor rather than packed at SPACING and centred. Four
    // agents in a room sized for a dozen were drawn as a tight cluster in the
    // middle of a large empty diamond, which read as an unfinished picture —
    // the desks take the room they have, and only fall back to the minimum
    // spacing when there genuinely is not enough of it.
    // Spread, but capped: at full spread four desks ended up one in each
    // corner of the room with nothing between them, which reads as four people
    // avoiding each other rather than as an office.
    const spread = (span, n) => Math.max(SPACING, Math.min(4, Math.floor((span - 3) / Math.max(1, n - 1)) || SPACING));
    const stepC = spread(ROOM.C, cols);
    const stepR = spread(ROOM.R, rows);
    const usedC = (cols - 1) * stepC, usedR = (rows - 1) * stepR;
    const padC = Math.max(1, Math.round((ROOM.C - 1 - usedC) / 2));
    const padR = Math.max(1, Math.round((ROOM.R - 1 - usedR) / 2));

    AGENTS.forEach((a, i) => {
        const gx = padC + (i % cols) * stepC;
        const gy = padR + Math.floor(i / cols) * stepR;
        const p = iso(gx, gy);
        DESKS.set(a.id, {
            gx, gy,
            desk: { x: p.x, y: p.y + TILE_H / 2 },
            seat: { x: p.x, y: p.y + TILE_H / 2 + 4 },
            screen: { x: p.x, y: p.y - 8 },
        });
    });
}

// ── drawing primitives ──────────────────────────────────────────────

function px(x, y, w, h, c) { ctx.fillStyle = c; ctx.fillRect(x | 0, y | 0, w | 0, h | 0); }

/** One isometric floor tile as a filled diamond. */
function tile(gx, gy, c) {
    const p = iso(gx, gy);
    ctx.fillStyle = c;
    ctx.beginPath();
    ctx.moveTo(p.x, p.y);
    ctx.lineTo(p.x + TILE_W / 2, p.y + TILE_H / 2);
    ctx.lineTo(p.x, p.y + TILE_H);
    ctx.lineTo(p.x - TILE_W / 2, p.y + TILE_H / 2);
    ctx.closePath();
    ctx.fill();
}

/** A box in the same projection as the floor: a diamond lid with a left and
 *  a right face. Everything in the room that is not a person is one of these,
 *  which is what keeps the furniture in the same world as the tiles. */
function isoSolid(x, y, hw, hh, h, top, left, right) {
    ctx.fillStyle = right;
    ctx.beginPath();
    ctx.moveTo(x + hw, y); ctx.lineTo(x, y + hh);
    ctx.lineTo(x, y + hh + h); ctx.lineTo(x + hw, y + h);
    ctx.closePath(); ctx.fill();

    ctx.fillStyle = left;
    ctx.beginPath();
    ctx.moveTo(x - hw, y); ctx.lineTo(x, y + hh);
    ctx.lineTo(x, y + hh + h); ctx.lineTo(x - hw, y + h);
    ctx.closePath(); ctx.fill();

    ctx.fillStyle = top;
    ctx.beginPath();
    ctx.moveTo(x, y - hh); ctx.lineTo(x + hw, y);
    ctx.lineTo(x, y + hh); ctx.lineTo(x - hw, y);
    ctx.closePath(); ctx.fill();
}

/* A 3x5 pixel alphabet, one number per glyph: each column is 5 bits, low bit
 * at the top. Fifteen pixels is enough to read a letter and small enough to
 * sit on a desk without becoming a label.
 *
 * Monograms, NOT logos. A nameplate with an initial says whose desk it is
 * just as clearly as a mark would, and this room has no business reproducing
 * anybody's trademark in pixels. */
const GLYPHS = {
    A: [0x1e, 0x05, 0x1e], B: [0x1f, 0x15, 0x0a], C: [0x0e, 0x11, 0x11],
    D: [0x1f, 0x11, 0x0e], E: [0x1f, 0x15, 0x15], F: [0x1f, 0x05, 0x05],
    G: [0x0e, 0x11, 0x1d], H: [0x1f, 0x04, 0x1f], I: [0x11, 0x1f, 0x11],
    J: [0x18, 0x10, 0x1f], K: [0x1f, 0x04, 0x1b], L: [0x1f, 0x10, 0x10],
    M: [0x1f, 0x02, 0x1f], N: [0x1f, 0x06, 0x1f], O: [0x0e, 0x11, 0x0e],
    P: [0x1f, 0x05, 0x02], Q: [0x0e, 0x19, 0x1e], R: [0x1f, 0x0d, 0x12],
    S: [0x12, 0x15, 0x09], T: [0x01, 0x1f, 0x01], U: [0x0f, 0x10, 0x0f],
    V: [0x07, 0x18, 0x07], W: [0x1f, 0x08, 0x1f], X: [0x1b, 0x04, 0x1b],
    Y: [0x03, 0x1c, 0x03], Z: [0x19, 0x15, 0x13],
};

function glyph(ch, x, y, col) {
    const g = GLYPHS[ch.toUpperCase()];
    if (!g) return 4;
    for (let c = 0; c < 3; c++)
        for (let r = 0; r < 5; r++)
            if (g[c] & (1 << r)) px(x + c, y + r, 1, 1, col);
    return 4;
}

/* ── sprites as grids ─────────────────────────────────────────────────
 *
 * Drawn, not computed.
 *
 * Every figure in this room started life as a stack of `px(x-5, top+7, 10, 9)`
 * calls, and that is a bad way to make pixel art for one reason: you cannot
 * see it while you write it. Each change was a guess, then a render, then a
 * screenshot, then another guess — which is why the monitor sat on nobody's
 * desk for three passes and the happy face came out furious.
 *
 * A grid is the picture. One character per pixel, a palette per sprite, and
 * what is written here is what appears. The desk marks were done this way and
 * came out right first time.
 *
 * Palette keys are shared across every body sprite so hair, accessories and
 * poses can be authored independently and stacked:
 *   .  transparent      1  skin        2  skin shadow   3  skin highlight
 *   4  hair             5  hair light  6  hair dark
 *   7  outfit           8  outfit dark 9  outfit light
 *   x  ink (eyes, line) o  white       -  neutral dark
 */
function drawGrid(rows, x, y, pal) {
    for (let r = 0; r < rows.length; r++) {
        const row = rows[r];
        for (let c = 0; c < row.length; c++) {
            const k = row[c];
            if (k === '.') continue;
            const col = pal[k];
            if (col) px(x + c, y + r, 1, 1, col);
        }
    }
}

/** Palette for one agent's body, derived from its avatar. */
function bodyPal(av) {
    return {
        '1': av.skin, '2': shade(av.skin, -0.20), '3': shade(av.skin, 0.10),
        '4': av.hairColor, '5': shade(av.hairColor, 0.16), '6': shade(av.hairColor, -0.26),
        '7': av.outfit, '8': shade(av.outfit, -0.30), '9': shade(av.outfit, 0.12),
        'x': '#241f30', 'o': '#ffffff', '-': '#1a1d33',
    };
}

/* The seated body: 16 wide, 18 tall, cut off where the desk crosses it.
 * Two rows of shading on the torso and a lit edge along the shoulders — the
 * thing the rectangle version never had, because volume at this size is one
 * pixel of highlight and one of shadow in the right place. */
const BODY_F = [
    '................',
    '................',
    '................',
    '................',
    '................',
    '................',
    '................',
    '................',
    '................',
    '.....2....2.....',
    '....999999......',
    '...977777789....',
    '..97777777789...',
    '..87777777779...',
    '..87777777778...',
    '..88777777788...',
    '..888888888888..',
    '..888888888888..',
];
const BODY_M = [
    '................',
    '................',
    '................',
    '................',
    '................',
    '................',
    '................',
    '................',
    '................',
    '....2......2....',
    '...99999999.....',
    '..97777777789...',
    '.977777777778...',
    '.877777777778...',
    '.877777777778...',
    '.887777777788...',
    '.8888888888888..',
    '.8888888888888..',
];

/** The head, 10x10, with a lit brow and a shaded jaw. */
const HEAD = [
    '...333333...',
    '..31111113..',
    '.3111111113.',
    '.1111111111.',
    '.1111111111.',
    '.1111111111.',
    '.1111111111.',
    '.2111111112.',
    '..21111112..',
    '...222222...',
];

/* Desk marks.
 *
 * Simplified pixel glyphs that identify which product sits at a desk — an
 * isometric cube for Unity, a ring for Unreal, a burst for Claude, and so on.
 * They are drawn in the room's own style at nine pixels square, which is
 * identification rather than reproduction; the marks themselves belong to
 * their respective owners, and this is a local dashboard saying who is here.
 *
 * Anything not in this table falls back to a monogram, so a new agent still
 * gets a readable plate on the day it first connects.
 */
const MARKS = {
    // Unity: the isometric cube its own mark is built from.
    unity: [
        '....#....',
        '...###...',
        '..##.##..',
        '.##...##.',
        '##..#..##',
        '#.##.##.#',
        '#..###..#',
        '.#..#..#.',
        '..#####..',
    ],
    // Unreal: a heavy ring with the U inside it.
    unreal: [
        '..#####..',
        '.##...##.',
        '##.....##',
        '#..#.#..#',
        '#..#.#..#',
        '#..#.#..#',
        '##..#..##',
        '.##...##.',
        '..#####..',
    ],
    // Claude: the radial burst.
    claude: [
        '....#....',
        '.#..#..#.',
        '..#.#.#..',
        '...###...',
        '##.###.##',
        '...###...',
        '..#.#.#..',
        '.#..#..#.',
        '....#....',
    ],
    // Codex: the hexagonal knot, reduced to a hexagon and its centre.
    codex: [
        '..#####..',
        '.#.....#.',
        '#..###..#',
        '#.#...#.#',
        '#.#...#.#',
        '#.#...#.#',
        '#..###..#',
        '.#.....#.',
        '..#####..',
    ],
    // Gemini: the four-point spark.
    gemini: [
        '....#....',
        '....#....',
        '...###...',
        '.#.###.#.',
        '##..#..##',
        '.#.###.#.',
        '...###...',
        '....#....',
        '....#....',
    ],
    // Grok: the slashed X.
    grok: [
        '#.......#',
        '.#.....#.',
        '..#...#..',
        '...#.#...',
        '....#....',
        '...#.#...',
        '..#...#..',
        '.#.....#.',
        '#.......#',
    ],
    // CluadeX: a diamond, for the one that is ours.
    cluadex: [
        '....#....',
        '...###...',
        '..##.##..',
        '.##...##.',
        '##.....##',
        '.##...##.',
        '..##.##..',
        '...###...',
        '....#....',
    ],
};

/** The mark for an agent, matched on the vendor prefix so `claude-code` and
 *  `codex-cli` get the same plate as their parent. */
function markFor(id) {
    const key = (id || '').toLowerCase();
    for (const k in MARKS) if (key === k || key.startsWith(k)) return MARKS[k];
    return null;
}

function drawMark(rows, x, y, col) {
    const hi = shade(col, 0.22), lo = shade(col, -0.35);
    for (let r = 0; r < rows.length; r++)
        for (let c = 0; c < rows[r].length; c++)
            if (rows[r][c] === '#')
                // One lit row at the top gives nine flat pixels a little form.
                px(x + c, y + r, 1, 1, r === 0 ? hi : (r > 6 ? lo : col));
}

/**
 * Two letters that are UNIQUE in this room.
 *
 * The obvious "first two characters" gives unity and unreal the same plate —
 * both UN — which is the one thing a nameplate must never do. So the second
 * letter is whichever position first tells this id apart from every other id
 * in the room, and it is recomputed when the roster changes rather than
 * hardcoded, because the next collision will be between two agents nobody has
 * connected yet.
 */
function monogramFor(id) {
    const clean = (s2) => (s2 || '').replace(/[^a-z]/gi, '').toUpperCase();
    const me = clean(id);
    if (!me) return '?';
    const others = AGENTS.map(a => clean(a.id)).filter(o => o && o !== me);
    for (let i = 1; i < Math.max(2, me.length); i++) {
        const cand = me[0] + me[i];
        if (!others.some(o => o.length > i && o[0] === me[0] && o[i] === me[i])) return cand;
    }
    return me.slice(0, 2);
}

/** The nameplate standing on the desk. Two letters at most: the point is to
 *  tell six desks apart at a glance, not to spell anything. */
function drawPlaque(x, yBase, id, col) {
    const mark = markFor(id);
    if (mark) {
        // A mark gets a stand rather than a plate: it is a thing propped up on
        // the desk, and a frame around it at this size just eats the glyph.
        px(x - 1, yBase - 2, 3, 3, '#12162c');
        drawMark(mark, x - 4, yBase - 12, col);
        px(x - 5, yBase, 11, 1, '#0b0e1c');
        return;
    }
    const mono = monogramFor(id);
    const w = mono.length * 4 + 3;
    px(x - (w >> 1), yBase - 8, w, 8, '#12162c');
    px(x - (w >> 1) + 1, yBase - 7, w - 2, 6, shade(col, -0.45));
    let gx = x - (w >> 1) + 2;
    for (const ch of mono) gx += glyph(ch, gx, yBase - 6, col);
    px(x - (w >> 1), yBase, w, 1, '#0b0e1c');
}

// ── the room ────────────────────────────────────────────────────────

function drawRoom() {
    LIGHTS = [];
    px(0, 0, CW, CH, '#070812');

    drawWalls();
    for (let gy = 0; gy < ROOM.R; gy++)
        for (let gx = 0; gx < ROOM.C; gx++)
            tile(gx, gy, (gx + gy) % 2 ? '#141838' : '#10142e');

    drawProps();

    // Light on the floor, warm under the lamp and cold from the screen. Two
    // sources rather than one flat glow: a single blue wash over everything is
    // what made the whole picture read as one dark colour with shapes in it.
    for (const a of AGENTS) {
        const d = DESKS.get(a.id);
        if (!d) continue;
        const off = a.state === 'offline';
        const cx = d.desk.x, cy = d.desk.y + 10;

        const warm = ctx.createRadialGradient(cx + 14, cy - 2, 2, cx + 14, cy - 2, off ? 20 : 40);
        warm.addColorStop(0, off ? 'rgba(255,190,120,0.04)' : 'rgba(255,186,110,0.17)');
        warm.addColorStop(1, 'rgba(255,186,110,0)');
        ctx.fillStyle = warm;
        ctx.fillRect(cx - 30, cy - 34, 80, 56);

        if (off) continue;
        const cold = ctx.createRadialGradient(cx - 8, cy - 4, 2, cx - 8, cy - 4, 30);
        cold.addColorStop(0, 'rgba(120,200,255,0.13)');
        cold.addColorStop(1, 'rgba(120,200,255,0)');
        ctx.fillStyle = cold;
        ctx.fillRect(cx - 40, cy - 34, 70, 52);
    }

    // Back to front, so a desk nearer the viewer covers the one behind it.
    const order = [...AGENTS].sort((a, b) => {
        const A = DESKS.get(a.id), B = DESKS.get(b.id);
        return (A.gx + A.gy) - (B.gx + B.gy);
    });
    for (const a of order) drawWorkstation(a);

    // Walkers last, so somebody crossing the room passes in FRONT of the desks
    // rather than through them.
    for (const a of order) drawWalker(a);

    if (BOSS && T < BOSS.until) drawBoss();
    drawPackets();
}

/** The two back walls, with the things an office has on them.
 *
 *  Drawn before the floor so the floor's front edge overlaps their base — the
 *  join is what makes the room look built rather than assembled. */
function drawWalls() {
    const H = WALL_H;
    const a = iso(0, ROOM.R);          // left corner
    const b = iso(0, 0);               // back corner
    const c = iso(ROOM.C, 0);          // right corner

    const quad = (p1, p2, fill) => {
        ctx.fillStyle = fill;
        ctx.beginPath();
        ctx.moveTo(p1.x, p1.y); ctx.lineTo(p2.x, p2.y);
        ctx.lineTo(p2.x, p2.y - H); ctx.lineTo(p1.x, p1.y - H);
        ctx.closePath(); ctx.fill();
    };
    // Clearly lighter than the floor or the room has no back, and the two
    // walls clearly different from each other or the corner disappears.
    quad(a, b, '#242a52');             // left wall, away from the window
    quad(b, c, '#333b73');             // right wall, catching what light there is

    // A skirting board along the bottom of each wall. Four pixels of trim is
    // the difference between a painted backdrop and a built room.
    const trim = (p1, p2, fill) => {
        ctx.fillStyle = fill;
        ctx.beginPath();
        ctx.moveTo(p1.x, p1.y); ctx.lineTo(p2.x, p2.y);
        ctx.lineTo(p2.x, p2.y - 4); ctx.lineTo(p1.x, p1.y - 4);
        ctx.closePath(); ctx.fill();
    };
    trim(a, b, '#1a1f40');
    trim(b, c, '#252c5a');

    // Where the walls meet, so the corner is a corner and not a seam.
    px(b.x - 1, b.y - H, 2, H, '#414a8e');

    // Props live in drawProps now. This function draws the ROOM itself:
    // two planes, a corner and a skirting board. It used to hang a window
    // and a whiteboard of its own, and once drawProps arrived the result
    // was two of each a few pixels apart — invisible at thumbnail size,
    // obvious the moment the picture was looked at properly.
}

/** The things that make it an office rather than a room with desks in it.
 *
 *  Placed relative to the room, so they move out of the way instead of being
 *  sat on when a fifth agent connects and the desk block grows. None of it is
 *  load-bearing information — it is here because four desks on an empty floor
 *  read as a diagram, and the owner asked for somewhere people work.
 */
function drawProps() {
    const A = iso(0, ROOM.R), B = iso(0, 0), C = iso(ROOM.C, 0);

    // ── on the left wall ────────────────────────────────────────────
    // The room deliberately runs off the sides of the frame, so anything hung
    // near the far end of a wall is hung off-screen. Every `t` below stays in
    // the middle stretch of its wall, which is the part the viewer can see.
    const onLeft = (t, h) => ({ x: A.x + (B.x - A.x) * t, y: A.y + (B.y - A.y) * t - h });
    const onRight = (t, h) => ({ x: B.x + (C.x - B.x) * t, y: B.y + (C.y - B.y) * t - h });

    // Whiteboard, with something half-erased on it.
    let q = onLeft(0.60, 28);
    px(q.x - 11, q.y, 23, 15, '#2a2f55');
    px(q.x - 10, q.y + 1, 21, 13, '#cdd6f5');
    px(q.x - 8, q.y + 4, 12, 1, '#6a7299');
    px(q.x - 8, q.y + 7, 8, 1, '#6a7299');
    px(q.x - 8, q.y + 10, 14, 1, '#e08a5a');
    px(q.x + 6, q.y + 13, 4, 1, '#8b93b8');        // pen tray

    // A poster.
    q = onLeft(0.36, 24);
    px(q.x - 7, q.y, 14, 18, '#1b2044');
    px(q.x - 6, q.y + 1, 12, 16, '#3b2f6b');
    px(q.x - 4, q.y + 4, 8, 1, '#ffb86c');
    px(q.x - 4, q.y + 12, 8, 4, '#6cf0ff');
    px(q.x - 2, q.y + 7, 4, 4, '#ffe27a');

    // ── on the right wall ───────────────────────────────────────────
    // The window. Night outside, because this room is mostly watched in the
    // evening and a bright window would blow the whole picture out.
    q = onRight(0.46, 30);
    px(q.x - 14, q.y, 28, 18, '#0a1226');
    px(q.x - 13, q.y + 1, 26, 16, '#0d1b3a');
    for (let i = 0; i < 9; i++)
        px(q.x - 11 + ((i * 11) % 24), q.y + 3 + ((i * 7) % 12), 1, 1, '#7fd4ff');
    px(q.x - 1, q.y + 1, 1, 16, '#16224a');        // mullion
    px(q.x - 13, q.y + 8, 26, 1, '#16224a');
    px(q.x - 15, q.y + 17, 30, 2, '#2b3566');      // sill

    // A clock, whose hands actually move.
    q = onRight(0.64, 26);
    px(q.x - 6, q.y - 1, 13, 13, '#8b93b8');
    px(q.x - 5, q.y, 11, 11, '#e6ebff');
    px(q.x - 4, q.y + 1, 9, 9, '#1b2044');
    px(q.x, q.y + 1, 1, 1, '#8b93b8'); px(q.x, q.y + 9, 1, 1, '#8b93b8');
    px(q.x - 4, q.y + 5, 1, 1, '#8b93b8'); px(q.x + 4, q.y + 5, 1, 1, '#8b93b8');
    const mm = (Date.now() / 60000) % 60, hh = (Date.now() / 3600000) % 12;
    const hand = (ang, len, col) => {
        const a2 = (ang - 0.25) * Math.PI * 2;
        px(q.x + Math.cos(a2) * len, q.y + 5 + Math.sin(a2) * len, 1, 1, col);
    };
    hand(mm / 60, 4, '#ffffff'); hand(hh / 12, 3, '#9aa3cc');

    // A shelf with books.
    q = onRight(0.28, 16);
    px(q.x - 9, q.y + 10, 19, 2, '#3a3050');
    const bc = ['#a8324f', '#2f7ea8', '#c9a227', '#3f7a4a', '#6b4fa8'];
    for (let i = 0; i < 6; i++)
        px(q.x - 8 + i * 3, q.y + 10 - (4 + (i * 3) % 4), 2, 4 + (i * 3) % 4, bc[i % bc.length]);

    // ── on the floor ────────────────────────────────────────────────
    // A plant in the back corner.
    const pl = iso(ROOM.C - 1, 0.6);
    isoSolid(pl.x, pl.y + 8, 5, 3, 6, '#5a4430', '#31241a', '#40301f');
    px(pl.x - 5, pl.y - 1, 3, 7, '#3e7a4a');
    px(pl.x + 2, pl.y - 4, 3, 10, '#4b9159');
    px(pl.x - 1, pl.y - 7, 3, 12, '#43824f');
    px(pl.x - 3, pl.y - 4, 2, 4, '#356b42');

    // A water cooler by the other wall.
    const wc = iso(0.6, ROOM.R - 1);
    isoSolid(wc.x, wc.y + 6, 5, 3, 12, '#3a4270', '#20253f', '#2c3358');
    px(wc.x - 4, wc.y - 10, 8, 8, '#6fd0e8');
    px(wc.x - 3, wc.y - 9, 6, 6, '#9fe4f5');

    // A rug where the owner stands, so the front of the room is floor rather
    // than unfinished space.
    const r0 = bossSpot();
    ctx.globalAlpha = 0.55;
    ctx.fillStyle = '#3a2d5e';
    ctx.beginPath();
    ctx.moveTo(r0.x, r0.y + 4 - TILE_H * 1.4);
    ctx.lineTo(r0.x + TILE_W * 1.4, r0.y + 4);
    ctx.lineTo(r0.x, r0.y + 4 + TILE_H * 1.4);
    ctx.lineTo(r0.x - TILE_W * 1.4, r0.y + 4);
    ctx.closePath(); ctx.fill();
    ctx.fillStyle = '#4a3a76';
    ctx.beginPath();
    ctx.moveTo(r0.x, r0.y + 4 - TILE_H * 0.8);
    ctx.lineTo(r0.x + TILE_W * 0.8, r0.y + 4);
    ctx.lineTo(r0.x, r0.y + 4 + TILE_H * 0.8);
    ctx.lineTo(r0.x - TILE_W * 0.8, r0.y + 4);
    ctx.closePath(); ctx.fill();
    ctx.globalAlpha = 1;
}

/** A soft dark diamond on the floor. Everything that stands in this room gets
 *  one — without contact shadows an isometric scene is a set of cut-outs. */
function castShadow(x, y, rx) {
    ctx.globalAlpha = 0.30;
    ctx.fillStyle = '#05060f';
    ctx.beginPath();
    ctx.moveTo(x, y - rx / 2);
    ctx.lineTo(x + rx, y);
    ctx.lineTo(x, y + rx / 2);
    ctx.lineTo(x - rx, y);
    ctx.closePath(); ctx.fill();
    ctx.globalAlpha = 1;
}

/** Desk, monitor, chair, and whoever is sitting in it. */
function drawWorkstation(a) {
    const d = DESKS.get(a.id);
    if (!d) return;
    const { x, y } = d.desk;
    const c = agentColor(a.id);
    const off = a.state === 'offline';
    const lit = a.state === 'working';

    // ONE anchor, and everything is measured from it.
    //
    // The lid is an isometric diamond, so its top edge is at a different
    // height for every horizontal offset — which is the trap this drawing fell
    // into twice. Placing the monitor and the figure relative to the lid's top
    // VERTEX put them both well above the surface they were supposed to be on.
    // deskTop(dx) answers the only question that matters: where is the desk,
    // under this thing, at this x.
    const HW = 17, HH = 8;
    const deskTop = (dx) => (y + 2) - HH * (1 - Math.min(Math.abs(dx), HW) / HW);

    // Pulled IN from the lid's corners. At dx = -8 the diamond has already
    // narrowed to a sliver, so a 15px monitor centred there had half its width
    // hanging over the edge — geometrically standing on the desk, and reading
    // as floating beside it.
    const mx = x - 6, hx = x + 6;      // screen left, person right
    const mBase = deskTop(-6), hBase = deskTop(6);

    castShadow(x, y + 14, 26);

    // Chair, then person, then the desk over them: seated is an overlap, not a
    // stacking order.
    px(hx - 8, hBase - 3, 16, 4, off ? '#1a1d33' : '#242845');
    px(hx - 8, hBase - 3, 16, 1, off ? '#22263f' : '#2f3455');
    // Out of their chair: the desk is drawn empty and they are drawn on the
    // floor further down, in front of everything.
    if (!off && !VISITS.has(a.id)) drawPerson(hx, hBase, c, a);

    // The desk. Proportioned to the figure rather than the room — at 23 half-
    // widths it was a slab with a small person behind it, which is a diorama,
    // not an office.
    isoSolid(x, y + 2, HW, HH, 6,
        off ? '#2a2f52' : '#3c4370',
        off ? '#171a2e' : '#252a4a',
        off ? '#1f2339' : '#2f3559');

    // Monitor, standing on the lid at its own x. The contact shadow is what
    // sells it: without one, anything resting on a flat isometric surface
    // reads as hovering a few pixels above it.
    ctx.globalAlpha = 0.35;
    px(mx - 6, mBase, 12, 2, '#0a0c18');
    ctx.globalAlpha = 1;
    px(mx - 2, mBase - 4, 4, 5, '#1b1f38');
    px(mx - 7, mBase - 15, 14, 11, '#10132a');
    px(mx - 6, mBase - 14, 12, 9, off ? '#0b0d1a' : (lit ? c : '#232a52'));
    if (!off && lit) {
        // Rows that scroll. The flicker is what makes a lit screen read as
        // "being used" rather than "switched on".
        ctx.globalAlpha = 0.42;
        for (let i = 0; i < 4; i++) {
            const w = 3 + ((T / 6 + i * 3) | 0) % 9;
            px(mx - 5, mBase - 13 + i * 2, w, 1, '#06121c');
        }
        ctx.globalAlpha = 1;
    }

    // Nameplate on the NEAR edge, facing the viewer. Its first home was the
    // far-left corner, which is behind the monitor from here — a nameplate
    // nobody can read is just a shape on a desk.
    drawPlaque(x - 7, deskTop(-7) + 9, a.id, off ? '#5b6390' : c);

    // Keyboard, under the hands on the front of the lid.
    px(hx - 6, hBase + 2, 12, 3, off ? '#242845' : '#2f3559');
    px(hx - 6, hBase + 2, 12, 1, off ? '#2a2f4c' : '#3a4270');

    // A desk lamp, which is where the warm light on the floor comes from.
    const lx = x - 15, lb = deskTop(-15);
    if (!off) {
        LIGHTS.push({ x: lx, y: lb - 7, r: 46, c: [255, 182, 104], i: 0.55 });
        if (lit) LIGHTS.push({ x: mx, y: mBase - 9, r: 34, c: hexToRgb(c), i: 0.42 });
    }
    px(lx, lb - 5, 2, 5, '#3a4270');
    px(lx - 2, lb - 9, 5, 4, off ? '#2c3352' : '#5a6398');
    if (!off) {
        px(lx - 1, lb - 6, 3, 1, '#ffd79a');
        ctx.globalAlpha = 0.5;
        px(lx - 3, lb - 5, 7, 5, '#ffb86c');
        ctx.globalAlpha = 1;
    }

    if (!off) {
        // A mug and a couple of sheets of paper. Four pixels each, and they do
        // more for "somebody works here" than another readout would.
        px(x + 13, deskTop(13) - 3, 3, 3, '#d4695a');
        px(x + 16, deskTop(13) - 2, 1, 1, '#d4695a');
        px(x + 4, deskTop(4) + 4, 6, 3, '#cdd6f5');
        px(x + 5, deskTop(5) + 3, 6, 3, '#e6ebff');
    }
}

/** ── avatars ──────────────────────────────────────────────────────────
 *
 * An agent chooses its own face with agent_avatar; anything it has not chosen
 * is DERIVED from its name rather than defaulted. That distinction is the
 * whole reason the room has characters in it instead of coloured dots: a grey
 * unset mannequin says "nobody has filled this in", a derived one says "this
 * is who that is", and it is the same on every machine because the derivation
 * is a hash.
 */
const SKINS = ['#f6d9bd', '#eec39a', '#d9a06b', '#b97a4e', '#8d5524', '#5c3a1e'];
const HAIRS = ['short', 'buzz', 'bob', 'long', 'ponytail', 'bun', 'curly', 'mohawk', 'bald'];
const HAIR_COLORS = ['#2b2430', '#4a3222', '#7a4a20', '#a8622c', '#c9a227', '#d8d8e0',
    '#6b4fa8', '#2f7ea8', '#a8324f', '#3f7a4a'];
const ACCESSORIES = ['none', 'glasses', 'headphones', 'cap', 'beanie', 'visor'];
const GENDERS = ['f', 'm', 'nb'];

function hash32(str) {
    let h = 2166136261 >>> 0;
    for (let i = 0; i < str.length; i++) { h ^= str.charCodeAt(i); h = Math.imul(h, 16777619) >>> 0; }
    return h;
}

/** The avatar to draw: what the agent chose, filled in from its name. */
function avatarOf(a) {
    const h = hash32(a.id || '');
    const v = a.avatar || {};
    return {
        gender: GENDERS.includes(v.gender) ? v.gender : GENDERS[h % GENDERS.length],
        hair: HAIRS.includes(v.hair) ? v.hair : HAIRS[(h >>> 3) % HAIRS.length],
        hairColor: v.hairColor || HAIR_COLORS[(h >>> 7) % HAIR_COLORS.length],
        skin: v.skin || SKINS[(h >>> 13) % SKINS.length],
        accessory: ACCESSORIES.includes(v.accessory) ? v.accessory : ACCESSORIES[(h >>> 17) % ACCESSORIES.length],
        outfit: v.outfit || agentColor(a.id),
    };
}

/** ── the character ────────────────────────────────────────────────────
 *
 * Drawn from the shoulders up, because that is all a desk leaves visible and
 * pretending otherwise wastes the pixels. Everything is built from flat
 * rectangles with one shade above and one below, which is what gives a pixel
 * figure volume without needing a single curve.
 */
function drawPerson(x, y, c, a) {
    const av = avatarOf(a);
    const em = liveEmote(a);
    const working = a.state === 'working';

    // A gesture overrides the typing bob: waving and typing at once reads as
    // a glitch rather than as enthusiasm.
    const gesture = em && em.gesture && em.gesture !== 'none' ? em.gesture : null;
    const bob = gesture ? 0 : (working ? ((T >> 2) % 2) : 0);

    // `y` is the desk surface under this figure. The lid crosses the body at
    // the chest, so the head sits fourteen pixels above it.
    const top = y - 17 - bob + (gesture === 'cheer' ? -((T >> 2) % 2) : 0);

    const outfit = av.outfit;
    const dark = shade(outfit, -0.32), mid = shade(outfit, -0.12), lite = shade(outfit, 0.10);
    const skin = av.skin, skinLo = shade(skin, -0.18), skinHi = shade(skin, 0.09);

    // Silhouette is what reads first at this size, so it is the one thing
    // gender actually changes: narrow and sloped, square, or between.
    const sw = av.gender === 'f' ? 6 : av.gender === 'm' ? 8 : 7;

    // Body and head from grids — see the note above drawGrid on why these are
    // written as pictures rather than built from rectangles.
    const pal = bodyPal(av);
    drawGrid(av.gender === 'f' ? BODY_F : BODY_M, x - 8, top, pal);
    drawGrid(HEAD, x - 6, top, pal);
    px(x - 6, top + 4, 1, 3, skin);                    // ears
    px(x + 5, top + 4, 1, 3, skin);

    drawHair(x, top, av);
    drawFace(x, top, em);
    drawAccessory(x, top, av);
    drawArms(x, top, a, av, gesture, bob, skin, mid, dark);
}

function drawHair(x, top, av) {
    const hc = av.hairColor, hl = shade(hc, 0.14), hd = shade(hc, -0.24);
    switch (av.hair) {
        case 'bald': px(x - 5, top, 10, 1, shade(av.skin, 0.14)); return;
        case 'buzz':
            px(x - 5, top - 1, 10, 3, hd); px(x - 5, top - 1, 10, 1, hc); return;
        case 'short':
            px(x - 5, top - 2, 10, 4, hc); px(x - 5, top - 2, 10, 1, hl);
            px(x - 6, top, 1, 4, hc); px(x + 5, top, 1, 4, hc);
            px(x + 2, top + 1, 3, 1, hl); return;
        case 'bob':
            px(x - 6, top - 2, 12, 5, hc); px(x - 6, top - 2, 12, 1, hl);
            px(x - 7, top + 1, 1, 8, hc); px(x + 6, top + 1, 1, 8, hc);
            px(x - 7, top + 9, 1, 1, hd); px(x + 6, top + 9, 1, 1, hd); return;
        case 'long':
            px(x - 6, top - 2, 12, 5, hc); px(x - 6, top - 2, 12, 1, hl);
            px(x - 8, top + 1, 2, 17, hc); px(x + 6, top + 1, 2, 17, hc);
            px(x - 8, top + 16, 2, 2, hd); px(x + 6, top + 16, 2, 2, hd); return;
        case 'ponytail':
            px(x - 5, top - 2, 10, 4, hc); px(x - 5, top - 2, 10, 1, hl);
            px(x + 5, top + 1, 2, 12, hc); px(x + 6, top + 10, 2, 4, hd); return;
        case 'bun':
            px(x - 5, top - 2, 10, 4, hc); px(x - 5, top - 2, 10, 1, hl);
            px(x - 3, top - 6, 6, 4, hc); px(x - 3, top - 6, 6, 1, hl); return;
        case 'curly':
            px(x - 6, top - 4, 12, 5, hc);
            px(x - 7, top - 2, 1, 5, hc); px(x + 6, top - 2, 1, 5, hc);
            px(x - 5, top - 5, 3, 1, hl); px(x + 2, top - 5, 3, 1, hl);
            px(x - 2, top - 6, 4, 1, hc); return;
        case 'mohawk':
            px(x - 2, top - 6, 4, 8, hc); px(x - 2, top - 6, 4, 1, hl);
            px(x - 5, top, 3, 1, hd); px(x + 2, top, 3, 1, hd); return;
    }
}

/** Moods, in a handful of pixels. Brows do most of the work — the eyes barely
 *  change, which is how faces actually read at this size. Eyes are ONE pixel
 *  tall by default: two made every character look furious. */
function drawFace(x, top, em) {
    const mood = em?.mood || 'neutral';
    const ink = '#2a2438';
    const soft = '#5b5170';
    const blink = (T % 230) < 5 && mood !== 'surprised';
    const ey = top + 4;

    if (blink) {
        px(x - 4, ey + 1, 3, 1, ink); px(x + 2, ey + 1, 3, 1, ink);
    } else if (mood === 'happy' || mood === 'proud') {
        // A proper ^ ^. A straight bar with one raised end read as a scowl,
        // which is how a cheering agent came out looking furious.
        px(x - 4, ey + 1, 1, 1, ink); px(x - 3, ey, 1, 1, ink); px(x - 2, ey + 1, 1, 1, ink);
        px(x + 2, ey + 1, 1, 1, ink); px(x + 3, ey, 1, 1, ink); px(x + 4, ey + 1, 1, 1, ink);
    } else if (mood === 'surprised') {
        px(x - 4, ey, 3, 3, ink); px(x + 2, ey, 3, 3, ink);
        px(x - 3, ey, 1, 1, '#ffffff'); px(x + 3, ey, 1, 1, '#ffffff');
    } else if (mood === 'tired') {
        px(x - 4, ey + 1, 3, 1, ink); px(x + 2, ey + 1, 3, 1, ink);
        px(x - 4, ey - 1, 3, 1, soft); px(x + 2, ey - 1, 3, 1, soft);
    } else {
        px(x - 4, ey, 2, 2, ink); px(x + 3, ey, 2, 2, ink);
    }

    // Brows.
    if (mood === 'thinking') { px(x - 4, ey - 3, 3, 1, ink); px(x + 2, ey - 4, 3, 1, ink); }
    else if (mood === 'annoyed' || mood === 'stuck') {
        px(x - 4, ey - 3, 3, 1, ink); px(x + 2, ey - 3, 3, 1, ink);
        px(x - 4, ey - 2, 1, 1, ink); px(x + 4, ey - 2, 1, 1, ink);
    } else if (mood === 'proud') { px(x - 4, ey - 4, 3, 1, ink); px(x + 2, ey - 4, 3, 1, ink); }

    // Mouth — small. A wide bar reads as a grimace on every mood.
    const my = top + 7;
    if (mood === 'happy' || mood === 'proud') {
        px(x - 1, my + 1, 3, 1, ink); px(x - 2, my, 1, 1, ink); px(x + 2, my, 1, 1, ink);
    } else if (mood === 'stuck' || mood === 'annoyed') {
        px(x - 1, my + 1, 3, 1, ink); px(x - 2, my + 2, 1, 1, ink); px(x + 2, my + 2, 1, 1, ink);
    } else if (mood === 'surprised') { px(x - 1, my, 2, 2, ink); }
    else if (mood === 'tired') { px(x - 1, my + 1, 3, 1, soft); }
    else px(x - 1, my + 1, 2, 1, ink);

    // The little marks that carry a whole state on their own.
    if (mood === 'thinking') {
        const k = (T >> 4) % 3;
        px(x + 8, top - 3 - k, 2, 2, '#cdd6f5');
    } else if (mood === 'stuck') {
        px(x + 7, top - 1, 2, 3, '#7fd4ff'); px(x + 7, top + 2, 2, 1, '#7fd4ff');
    } else if ((mood === 'proud' || mood === 'happy') && ((T >> 3) % 2)) {
        px(x + 8, top - 4, 1, 3, '#ffe27a'); px(x + 7, top - 3, 3, 1, '#ffe27a');
    }
}

function drawAccessory(x, top, av) {
    switch (av.accessory) {
        case 'glasses': {
            // FRAMES, not lenses. Filled rectangles hid the eyes completely and
            // the face turned into a visor — the one accessory that must not
            // cover the feature the mood is expressed with.
            const fr = '#3a3f66';
            const lens = (lx) => {
                px(lx, top + 3, 5, 1, fr); px(lx, top + 6, 5, 1, fr);
                px(lx, top + 4, 1, 2, fr); px(lx + 4, top + 4, 1, 2, fr);
                ctx.globalAlpha = 0.22; px(lx + 1, top + 4, 3, 2, '#9fd9ff'); ctx.globalAlpha = 1;
            };
            lens(x - 6); lens(x + 1);
            px(x - 1, top + 4, 2, 1, fr);      // bridge
            return;
        }
        case 'headphones':
            // A band and two slim cups — a solid block over the ears read as a
            // helmet and hid half the face.
            px(x - 7, top + 2, 2, 5, '#2c3350'); px(x + 6, top + 2, 2, 5, '#2c3350');
            px(x - 6, top - 3, 12, 1, '#39416b');
            px(x - 7, top - 2, 1, 4, '#39416b'); px(x + 6, top - 2, 1, 4, '#39416b');
            px(x - 7, top + 3, 1, 1, '#6cf0ff'); return;
        case 'cap':
            px(x - 6, top - 3, 12, 3, '#2f7ea8'); px(x - 6, top - 3, 12, 1, shade('#2f7ea8', .18));
            px(x - 10, top, 7, 1, '#276a8d'); return;
        case 'beanie':
            px(x - 6, top - 5, 12, 6, '#a8324f'); px(x - 6, top - 5, 12, 1, shade('#a8324f', .2));
            px(x - 6, top, 12, 1, '#7d2540'); px(x - 1, top - 7, 2, 2, '#c8506c'); return;
        case 'visor':
            px(x - 6, top + 2, 12, 4, '#12162e');
            px(x - 5, top + 3, 10, 1, '#6cf0ff'); return;
    }
}

/** Arms: typing, folded, or doing whatever the gesture says. */
function drawArms(x, top, a, av, gesture, bob, skin, mid, dark) {
    const working = a.state === 'working';
    const sw = av.gender === 'f' ? 6 : av.gender === 'm' ? 8 : 7;
    const wave = (T >> 2) % 2;
    const sleeve = mid, hand = skin;

    const arm = (ax, ay, len) => { px(ax, ay, 3, len, sleeve); px(ax, ay + len, 3, 3, hand); };

    switch (gesture) {
        case 'wave':
            arm(x - sw - 3, top + 13, 3);
            px(x + sw, top + 4 - wave, 3, 8, sleeve); px(x + sw, top + 1 - wave, 3, 3, hand);
            return;
        case 'thumbsup':
            arm(x - sw - 3, top + 13, 3);
            px(x + sw, top + 8, 3, 5, sleeve); px(x + sw, top + 5, 3, 3, hand);
            px(x + sw + 1, top + 2, 1, 3, hand);
            return;
        case 'cheer':
            px(x - sw - 3, top + 2 - wave, 3, 10, sleeve); px(x - sw - 3, top - 1 - wave, 3, 3, hand);
            px(x + sw, top + 2 + wave, 3, 10, sleeve); px(x + sw, top - 1 + wave, 3, 3, hand);
            return;
        case 'shrug':
            px(x - sw - 4, top + 11, 3, 4, sleeve); px(x - sw - 4, top + 8, 3, 3, hand);
            px(x + sw + 1, top + 11, 3, 4, sleeve); px(x + sw + 1, top + 8, 3, 3, hand);
            return;
        case 'facepalm':
            arm(x - sw - 3, top + 13, 3);
            px(x + sw - 1, top + 7, 3, 6, sleeve); px(x + 1, top + 3, 5, 5, hand);
            return;
        case 'stretch':
            px(x - sw - 3, top, 3, 12, sleeve); px(x - sw - 3, top - 3, 3, 3, hand);
            px(x + sw, top, 3, 12, sleeve); px(x + sw, top - 3, 3, 3, hand);
            return;
        case 'point':
            arm(x - sw - 3, top + 13, 3);
            px(x + sw, top + 11, 6, 3, sleeve); px(x + sw + 6, top + 11, 3, 2, hand);
            return;
        default: {
            const ay = working ? top + 14 + (bob ? 0 : 1) : top + 16;
            arm(x - sw - 2, ay - 3, 3);
            arm(x + sw - 1, ay - 3, 3);
            return;
        }
    }
}

/** The agent's live emote, or null. Expiry is checked here as well as on the
 *  host, because the room keeps rendering between polls and a cheer that
 *  outlived its two seconds is worse than no cheer at all. */
function liveEmote(a) {
    const e = a.emote;
    if (!e) return null;
    if (e.expiresUtc && Date.parse(e.expiresUtc) < Date.now()) return null;
    return e;
}

/**
 * Somebody walking to another desk, standing there, and walking back.
 *
 * Three phases over the visit: out, talk, back. Eased so they slow down as
 * they arrive — constant velocity reads as a sprite being dragged rather than
 * as a person crossing a room.
 */
function drawWalker(a) {
    const v = VISITS.get(a.id);
    if (!v) return;
    const from = DESKS.get(a.id), to = DESKS.get(v.to);
    if (!from || !to) { VISITS.delete(a.id); return; }

    const k = (performance.now() - v.t0) / v.dur;
    if (k >= 1) { VISITS.delete(a.id); return; }

    // Stand a little in FRONT of each desk rather than on it.
    const A = { x: from.desk.x, y: from.desk.y + 16 };
    const B = { x: to.desk.x, y: to.desk.y + 16 };

    let p, walking, phase;
    if (k < 0.3) { phase = k / 0.3; p = lerpPt(A, B, ease(phase)); walking = true; }
    else if (k < 0.7) { p = B; walking = false; }
    else { phase = (k - 0.7) / 0.3; p = lerpPt(B, A, ease(phase)); walking = true; }

    castShadow(p.x, p.y + 1, 7);
    drawStanding(p.x, p.y, a, walking, B.x < A.x);

    // While they are over there, they are talking to whoever sits there.
    if (!walking && !BUBBLES.some(b => b.agent === a.id))
        BUBBLES.push({ agent: a.id, text: '…', color: agentColor(a.id), until: T + 90 });
}

const ease = (t) => t < 0.5 ? 2 * t * t : 1 - Math.pow(-2 * t + 2, 2) / 2;
const lerpPt = (A, B, t) => ({ x: A.x + (B.x - A.x) * t, y: A.y + (B.y - A.y) * t });

/** The same character, on their feet. The seated sprite is a bust — it has no
 *  legs, because a desk hides them; crossing a room needs the rest. */
function drawStanding(x, yFloor, a, walking, facingLeft) {
    const av = avatarOf(a);
    const outfit = av.outfit;
    const mid = shade(outfit, -0.12), dark = shade(outfit, -0.34), lite = shade(outfit, 0.10);
    const skin = av.skin;
    const step = walking ? ((T >> 2) % 4) : 0;      // 4-frame walk cycle
    const lift = (step === 1 || step === 3) ? 1 : 0;

    const top = yFloor - 30 - lift;
    const sw = av.gender === 'f' ? 6 : av.gender === 'm' ? 8 : 7;

    // Legs, alternating.
    const la = step === 1 ? 2 : step === 3 ? -2 : 0;
    px(x - 4 + la, yFloor - 9, 3, 9, dark);
    px(x + 1 - la, yFloor - 9, 3, 9, shade(outfit, -0.42));
    px(x - 4 + la, yFloor - 1, 4, 2, '#1d2138');
    px(x + 1 - la, yFloor - 1, 4, 2, '#1d2138');

    px(x - sw, top + 11, sw * 2, 10, mid);
    px(x - sw, top + 11, sw * 2, 1, lite);
    px(x - 2, top + 9, 5, 3, shade(skin, -0.18));

    px(x - 5, top, 10, 10, skin);
    px(x - 5, top, 10, 1, shade(skin, 0.09));
    drawHair(x, top, av);
    drawFace(x, top, liveEmote(a));
    drawAccessory(x, top, av);

    // Arms swing opposite the legs.
    const aa = walking ? (step === 1 ? 2 : step === 3 ? -2 : 0) : 0;
    px(x - sw - 3, top + 12 - aa, 3, 8, mid);
    px(x - sw - 3, top + 20 - aa, 3, 3, skin);
    px(x + sw, top + 12 + aa, 3, 8, mid);
    px(x + sw, top + 20 + aa, 3, 3, skin);
}

/** The owner, standing in the middle of the room. Not at a desk, on purpose —
 *  the boss walks in, says the thing, and everyone else is still sitting. */
function bossSpot() {
    const n = AGENTS.length || 1;
    const cols = deskCols(n), rows = Math.ceil(n / cols);
    const c = (cols - 1) * SPACING / 2;
    return iso(c + 1.4, (rows - 1) * SPACING + 2.6);
}

function drawBoss() {
    const p = bossSpot();
    const c = FIXED.owner;
    px(p.x - 5, p.y + 6, 10, 8, shade(c, -0.3));
    px(p.x - 3, p.y, 6, 6, '#f0cfae');
    px(p.x - 4, p.y - 1, 8, 2, shade(c, -0.6));
    px(p.x - 2, p.y + 3, 1, 1, '#2a2438');
    px(p.x + 1, p.y + 3, 1, 1, '#2a2438');
    // Shoulders shift slightly — standing, not a statue.
    const sway = ((T >> 4) % 2) ? 1 : 0;
    px(p.x - 7 + sway, p.y + 7, 2, 4, '#f0cfae');
    px(p.x + 5 + sway, p.y + 7, 2, 4, '#f0cfae');
}

/** Messages in flight, as a dot travelling desk to desk. */
function drawPackets() {
    const now = performance.now();
    for (let i = PACKETS.length - 1; i >= 0; i--) {
        const p = PACKETS[i];
        const k = (now - p.t0) / p.ms;
        if (k >= 1) { PACKETS.splice(i, 1); continue; }
        const a = DESKS.get(p.from), b = DESKS.get(p.to);
        if (!a || !b) { PACKETS.splice(i, 1); continue; }
        // A slight arc, so two messages crossing in opposite directions do not
        // draw over each other.
        const x = a.screen.x + (b.screen.x - a.screen.x) * k;
        const y = a.screen.y + (b.screen.y - a.screen.y) * k - Math.sin(k * Math.PI) * 14;
        px(x - 1, y - 1, 3, 3, p.color);
        ctx.globalAlpha = 0.4;
        px(x - 2, y - 2, 5, 5, p.color);
        ctx.globalAlpha = 1;
    }
}

function hexToRgb(h) {
    if (!h || h[0] !== '#') return [140, 190, 255];
    const n = parseInt(h.slice(1), 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

/**
 * Blit the scene up, then light it.
 *
 * Every step here is at DEVICE resolution, which is the whole point — a glow
 * drawn on the logical canvas would be a staircase of chunky rings, and the
 * first version of this room had exactly that.
 */
function present() {
    const W = cv.width, H = cv.height;
    vctx.imageSmoothingEnabled = false;
    vctx.drawImage(scene, 0, 0, W, H);

    // Bloom. `lighter` so overlapping lamps build up rather than flatten each
    // other, and a soft radial falloff so the pixels underneath stay readable
    // through it instead of being washed out.
    vctx.globalCompositeOperation = 'lighter';
    for (const L of LIGHTS) {
        const cx = L.x * SCALE, cy = L.y * SCALE, r = L.r * SCALE;
        const g = vctx.createRadialGradient(cx, cy, 1, cx, cy, r);
        const [R, G, B] = L.c;
        g.addColorStop(0, `rgba(${R},${G},${B},${L.i})`);
        g.addColorStop(0.35, `rgba(${R},${G},${B},${L.i * 0.28})`);
        g.addColorStop(1, `rgba(${R},${G},${B},0)`);
        vctx.fillStyle = g;
        vctx.fillRect(cx - r, cy - r, r * 2, r * 2);
    }
    vctx.globalCompositeOperation = 'source-over';

    // A vignette, and a cool grade in the corners. This is what stops a flat
    // field of navy reading as flat: the eye needs somewhere darker to judge
    // the lit parts against.
    const vg = vctx.createRadialGradient(W / 2, H * 0.46, Math.min(W, H) * 0.32,
                                         W / 2, H * 0.46, Math.max(W, H) * 0.78);
    vg.addColorStop(0, 'rgba(0,0,0,0)');
    vg.addColorStop(0.6, 'rgba(4,5,14,0.30)');
    vg.addColorStop(1, 'rgba(3,4,11,0.80)');
    vctx.fillStyle = vg;
    vctx.fillRect(0, 0, W, H);
}

/** A faint CRT banding over the whole room. Cheap, and it ties the procedural
 *  sprites together into one picture instead of a set of drawings. */
function scanlines() {
    // Drawn on the PRESENTED image at device resolution: one-logical-pixel
    // bands would be SCALE pixels thick on screen and read as blinds.
    vctx.globalAlpha = 0.05;
    vctx.fillStyle = '#000';
    for (let y = 0; y < cv.height; y += 3) vctx.fillRect(0, y, cv.width, 1);
    vctx.globalAlpha = 1;
}

function shade(hex, k) {
    if (!hex.startsWith('#')) return hex;
    const n = parseInt(hex.slice(1), 16);
    const f = (v) => Math.max(0, Math.min(255, Math.round(v + 255 * k)));
    return `rgb(${f((n >> 16) & 255)},${f((n >> 8) & 255)},${f(n & 255)})`;
}

// ── overlay: name plates and bubbles ────────────────────────────────

function drawOverlay() {
    const r = cv.getBoundingClientRect();
    const sx = r.width / CW, sy = r.height / CH;   // CSS px per logical px
    const html = [];

    for (const a of AGENTS) {
        const d = DESKS.get(a.id);
        if (!d) continue;
        const doing = a.state === 'offline' ? 'ไม่อยู่'
            : a.state === 'working' ? (a.lastTool || 'ทำงานอยู่')
                : 'ว่าง';
        html.push(
            `<div class="plate${a.state === 'offline' ? ' is-off' : ''}" ` +
            `style="--pc:${agentColor(a.id)};left:${(d.desk.x * sx).toFixed(1)}px;top:${((d.desk.y + 14) * sy).toFixed(1)}px">` +
            `<span class="who">${esc(label(a.label || a.id))}</span>` +
            (a.spawned ? `<span class="badge">AUTO</span>` : '') +
            (a.pending ? `<span class="badge">${a.pending}</span>` : '') +
            `<span class="doing">${esc(doing)}</span></div>`);
    }

    for (const b of BUBBLES) {
        if (T >= b.until) continue;
        const d = DESKS.get(b.agent);
        if (!d) continue;
        html.push(
            `<div class="bubble" style="--bc:${b.color};left:${(d.screen.x * sx).toFixed(1)}px;` +
            `top:${((d.screen.y - 6) * sy).toFixed(1)}px">${esc(b.text)}</div>`);
    }

    if (BOSS && T < BOSS.until) {
        const p = bossSpot();
        html.push(
            `<div class="bubble" style="--bc:${FIXED.owner};left:${(p.x * sx).toFixed(1)}px;` +
            `top:${((p.y - 6) * sy).toFixed(1)}px">${esc(BOSS.text)}</div>`);
    }

    overlay.innerHTML = html.join('');
    while (BUBBLES.length && T >= BUBBLES[0].until) BUBBLES.shift();
}

// ── 16-bit sound ────────────────────────────────────────────────────
/*
 * Synthesised, never sampled. A square wave with a hard envelope IS how these
 * machines made sound, so generating it is more faithful than a recording of
 * one — and it means no audio asset to ship, nothing to keep decoding, and no
 * way for an agent to make the owner's speakers play something arbitrary: it
 * can name a sound from a fixed list and nothing else.
 */
let AC = null;
const SOUND_ON_KEY = 'brainx.office.sound';
let SOUND_ON = (() => { try { return localStorage.getItem(SOUND_ON_KEY) !== 'off'; } catch { return true; } })();

/** Browsers refuse to start audio until the page has been interacted with, so
 *  the context is created on the first click or key rather than at load. */
function audio() {
    if (AC) return AC;
    try { AC = new (window.AudioContext || window.webkitAudioContext)(); } catch { AC = null; }
    return AC;
}
addEventListener('pointerdown', audio, { once: true });
addEventListener('keydown', audio, { once: true });

/** One note. `type` picks the chip voice: square for melody, triangle for the
 *  soft low notes, sawtooth for anything that should feel like an error. */
function note(freq, start, dur, type = 'square', gain = 0.07) {
    const ac = audio();
    if (!ac) return;
    const t0 = ac.currentTime + start;
    const o = ac.createOscillator(), g = ac.createGain();
    o.type = type;
    o.frequency.setValueAtTime(freq, t0);
    // A hard attack and an exponential tail — the envelope is most of what
    // makes a square wave read as "chiptune" rather than as a test tone.
    g.gain.setValueAtTime(0.0001, t0);
    g.gain.exponentialRampToValueAtTime(gain, t0 + 0.006);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    o.connect(g); g.connect(ac.destination);
    o.start(t0); o.stop(t0 + dur + 0.02);
}

const N = { c4: 261.6, e4: 329.6, g4: 392.0, a4: 440.0, c5: 523.3, e5: 659.3, g5: 784.0, c6: 1046.5 };

function playSound(name) {
    if (!SOUND_ON || !name || name === 'none') return;
    switch (name) {
        case 'ping': note(N.e5, 0, 0.09); break;
        case 'ok': note(N.c5, 0, 0.07); note(N.g5, 0.07, 0.10); break;
        case 'done': note(N.c5, 0, 0.06); note(N.e5, 0.06, 0.06); note(N.g5, 0.12, 0.14); break;
        case 'levelup':
            note(N.c5, 0, 0.05); note(N.e5, 0.05, 0.05);
            note(N.g5, 0.10, 0.05); note(N.c6, 0.15, 0.20); break;
        case 'oops': note(N.g4, 0, 0.09, 'sawtooth'); note(N.c4, 0.09, 0.16, 'sawtooth'); break;
        case 'hmm': note(N.c4, 0, 0.14, 'triangle', 0.09); note(N.e4 * 0.97, 0.14, 0.18, 'triangle', 0.09); break;
        case 'alert': note(N.a4, 0, 0.06, 'square', 0.09); note(N.a4, 0.10, 0.06, 'square', 0.09); break;
        case 'type': note(1200, 0, 0.02, 'square', 0.03); break;
    }
}

// ── side panel ──────────────────────────────────────────────────────

function renderLog() {
    const el = document.getElementById('log');
    // Stick to newest unless the reader has scrolled back to re-read. Measured
    // BEFORE the rebuild: an emptied list reports no scroll at all.
    const stick = el.clientHeight === 0 || el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    const open = new Set([...el.querySelectorAll('li.open')].map(li => li.dataset.id));

    el.innerHTML = '';
    for (const m of MESSAGES) {
        const li = document.createElement('li');
        li.dataset.id = m.id || '';
        if (open.has(m.id)) li.classList.add('open');
        if (m.pending) li.classList.add('pending');
        if (m.from === 'owner') li.classList.add('boss');
        li.style.setProperty('--lc', agentColor(m.from));
        li.innerHTML =
            `<div class="lh"><span class="from">${esc(label(m.from))}</span>` +
            `<span>→ ${esc(label(m.to))}</span>` +
            (m.topic ? `<span>· ${esc(m.topic)}</span>` : '') +
            `<span class="t">${esc(m.ts || '')}</span></div>` +
            `<div class="lb">${esc(m.body || '')}</div>` +
            attachmentsHtml(m.attachments);
        li.addEventListener('click', (e) => {
            if (e.target.closest('.atts')) return;   // opening a file is not "expand"
            li.classList.toggle('open');
        });
        el.appendChild(li);
    }
    document.getElementById('side-count').textContent = MESSAGES.length || '—';
    if (stick) el.scrollTop = el.scrollHeight;
}

function attachmentsHtml(atts) {
    if (!atts || !atts.length) return '';
    const parts = atts.map(a => {
        if (a.error) return `<span class="file bad" title="${esc(a.error)}">⚠ ${esc(a.name || 'file')}</span>`;
        const url = fileUrl(a.path);
        if (a.kind === 'image' && url)
            return `<img src="${esc(url)}" alt="${esc(a.name)}" data-open="${esc(a.path)}" loading="lazy">`;
        return `<span class="file" data-open="${esc(a.path)}">📎 ${esc(a.name)}${a.bytes ? ' · ' + kb(a.bytes) : ''}</span>`;
    });
    return `<div class="atts">${parts.join('')}</div>`;
}

function renderDecisions() {
    const el = document.getElementById('decisions');
    el.innerHTML = '';
    for (const d of DECISIONS) {
        if (d.status !== 'open') continue;
        const li = document.createElement('li');
        const opts = (d.options || []).map(o =>
            `<button data-id="${esc(d.id)}" data-answer="${esc(o)}">${esc(o)}</button>`).join('');
        li.innerHTML =
            `<div class="dmeta">${esc(d.agent || '?')} รออยู่${d.work ? ' · ' + esc(d.work) : ''}</div>` +
            `<div class="dq">${esc(d.question || '')}</div>` +
            `<div class="opts">${opts}<button class="other" data-id="${esc(d.id)}" data-answer="">อื่น ๆ…</button></div>`;
        el.appendChild(li);
    }
}

// ── talking to the host ─────────────────────────────────────────────

function post(msg) { try { window.chrome?.webview?.postMessage(msg); } catch { /* not hosted */ } }

/** Where an attachment actually lives. The payload carries a path relative to
 *  the bus root; the host tells us the root once, because the page has no way
 *  to know where the vault is and a wrong guess renders every picture broken. */
let BUS_URL = '';
function fileUrl(rel) { return rel && BUS_URL ? BUS_URL + rel : ''; }
function kb(b) { return b > 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.max(1, Math.round(b / 1024)) + ' KB'; }

function onMessage(evt) {
    const m = evt.data;
    if (!m || typeof m !== 'object') return;
    if (m.type !== 'officeState') return;
    apply(m.payload || {});
}

function apply(p) {
    if (typeof p.busUrl === 'string') BUS_URL = p.busUrl;
    const hadAgents = AGENTS.length;
    AGENTS = (p.agents || []).slice().sort((a, b) => a.id.localeCompare(b.id));
    if (AGENTS.length !== hadAgents) layoutDesks();

    MESSAGES = p.messages || [];
    DECISIONS = p.decisions || [];

    // Perform only what is NEW. The first payload is the backlog, and replaying
    // a day of it as bubbles would say "all of this just happened".
    const fresh = MESSAGES.filter(m => m.id && !SEEN.has(m.id));
    for (const m of MESSAGES) if (m.id) SEEN.add(m.id);
    if (PRIMED) {
        for (const m of fresh.slice(-4)) {
            const c = agentColor(m.from);
            if (m.from === 'owner') {
                BOSS = { until: T + 260, text: firstLine(m.body) };
            } else {
                BUBBLES.push({ agent: m.from, text: firstLine(m.body), color: c, until: T + 240 });
            }
            if (DESKS.has(m.from) && DESKS.has(m.to)) {
                PACKETS.push({ from: m.from, to: m.to, color: c, t0: performance.now(), ms: 900 });
                // One visit at a time per agent, and never to your own desk.
                const sender = AGENTS.find(x => x.id === m.from);
                if (m.from !== m.to && sender && sender.state !== 'offline' && !VISITS.has(m.from))
                    VISITS.set(m.from, { to: m.to, t0: performance.now(), dur: 9000 });
            }
        }
    }
    PRIMED = true;

    // Emotes: play each one ONCE. Keyed on the emote's own timestamp because
    // the payload repeats every two seconds and the same cheer would otherwise
    // fire twenty times before it expired.
    for (const a of AGENTS) {
        const e = liveEmote(a);
        if (!e || !e.atUtc) continue;
        const key = a.id + '|' + e.atUtc;
        if (EMOTES_PLAYED.has(key)) continue;
        EMOTES_PLAYED.add(key);
        if (PRIMED) {
            playSound(e.sound);
            if (e.say) BUBBLES.push({ agent: a.id, text: firstLine(e.say), color: agentColor(a.id), until: T + 240 });
        }
    }
    if (EMOTES_PLAYED.size > 200) EMOTES_PLAYED.clear();

    const online = AGENTS.filter(a => a.state !== 'offline').length;
    document.getElementById('room-sub').textContent =
        `${online} อยู่ในห้อง · ${AGENTS.length} ที่นั่ง`;
    document.getElementById('room-dot').classList.toggle('off', online === 0);

    renderLog();
    renderDecisions();
}

function firstLine(s) {
    const t = (s || '').replace(/\s+/g, ' ').trim();
    return t.length > 90 ? t.slice(0, 89) + '…' : (t || '…');
}

function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, c =>
        ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// ── the owner's line ────────────────────────────────────────────────

document.getElementById('say').addEventListener('submit', (e) => {
    e.preventDefault();
    const box = document.getElementById('say-text');
    const text = box.value.trim();
    if (!text) return;
    // The host writes it to the bus as `owner` — the page cannot, and must not
    // be able to: an identity the agents trust has to come from a process the
    // owner controls, not from a document.
    post({ type: 'officeSay', text });
    box.value = '';
    // Show the boss immediately rather than waiting for the next poll. The
    // round trip is under two seconds, but a send that looks like nothing
    // happened gets sent twice.
    BOSS = { until: T + 260, text: firstLine(text) };
});

document.getElementById('decisions').addEventListener('click', (e) => {
    const b = e.target.closest('button');
    if (!b) return;
    const answer = b.dataset.answer || prompt('ตอบว่าอะไร?');
    if (!answer) return;
    post({ type: 'officeAnswer', id: b.dataset.id, answer });
    b.closest('li')?.remove();
});

document.getElementById('log').addEventListener('click', (e) => {
    const t = e.target.closest('[data-open]');
    if (t) post({ type: 'officeOpen', path: t.dataset.open });
});

// ── demo mode ───────────────────────────────────────────────────────

/** `?demo=1` fills the room without a host, so the art and the layout can be
 *  looked at (and screenshotted) without the WPF app running. */
function demo() {
    const now = Date.now();
    const em = (mood, gesture, sound, say) => ({
        mood, gesture, sound, say, atUtc: new Date().toISOString(),
        expiresUtc: new Date(Date.now() + 3600e3).toISOString(),
    });
    const agents = [
        { id: 'claude', label: 'Claude', state: 'working', lastTool: 'brain_search', pending: 0,
          avatar: { gender: 'f', hair: 'long', hairColor: '#4a3222', skin: '#eec39a', accessory: 'glasses' },
          emote: em('thinking', 'none', 'none') },
        { id: 'codex', label: 'Codex', state: 'working', lastTool: 'agent_inbox', pending: 2, spawned: true,
          avatar: { gender: 'm', hair: 'short', hairColor: '#2b2430', skin: '#d9a06b', accessory: 'headphones' },
          emote: em('happy', 'thumbsup', 'none', 'ชุดภาพเสร็จแล้ว') },
        { id: 'cluadex', label: 'CluadeX', state: 'idle', lastTool: '', pending: 0,
          avatar: { gender: 'nb', hair: 'curly', hairColor: '#6b4fa8', skin: '#b97a4e', accessory: 'beanie' },
          emote: em('tired', 'none', 'none') },
        { id: 'gemini', label: 'Gemini', state: 'offline', lastTool: '', pending: 1,
          avatar: { gender: 'f', hair: 'bun', hairColor: '#c9a227', skin: '#f6d9bd', accessory: 'none' } },
        // The two BRIDGES. They never write presence — the brain calls out to
        // them — so in the real room their seats come from mcp-bridges.json.
        { id: 'unity', label: 'Unity', state: 'idle', lastTool: 'manage_scene', pending: 0, bridge: true,
          avatar: { gender: 'm', hair: 'buzz', hairColor: '#2b2430', skin: '#f6d9bd', accessory: 'visor', outfit: '#c9ccd6' } },
        { id: 'unreal', label: 'Unreal', state: 'working', lastTool: 'call_tool', pending: 0, bridge: true,
          avatar: { gender: 'nb', hair: 'short', hairColor: '#d8d8e0', skin: '#8d5524', accessory: 'cap', outfit: '#3a3f55' } },
    ];
    const mk = (i, from, to, body, extra) => ({
        id: 'd' + i, at: now - (9 - i) * 60000,
        ts: new Date(now - (9 - i) * 60000).toTimeString().slice(0, 5),
        from, to, body, ...extra,
    });
    const messages = [
        mk(1, 'claude', 'codex', 'ชุดภาพ 2 ต้อง 256×256 seamless และ alpha ตรงสเปก', { topic: 'gpuxmine-wpf' }),
        mk(2, 'codex', 'claude', 'รับทราบ กำลังทำไฟล์ทั้ง 5 ใบและตรวจขนาด/alpha/รอยต่อ', { topic: 'gpuxmine-wpf' }),
        mk(3, 'codex', 'claude', 'imagegen ส่ง 1254×1254 แทน 256×256 — ขอเจ้าของตัดสินใจ', { topic: 'gpuxmine-wpf', pending: true }),
        mk(4, 'owner', 'all', 'ใช้วิธีวาดด้วยโค้ดเหมือนชุดแรก', {}),
    ];
    const decisions = [{
        id: 'ask-demo', agent: 'codex', work: 'gpuxmine-wpf', status: 'open',
        question: 'imagegen ส่งขนาดไม่ตรงสเปก จะวาดด้วยโค้ดเหมือนชุดแรก หรือใช้ imagegen แล้ว normalize?',
        options: ['วาดด้วยโค้ด', 'imagegen + normalize'],
    }];
    apply({ agents, messages, decisions });

    // A message every few seconds, so the bubbles and the packets can be seen
    // doing what they do on a live vault.
    let i = 100;
    setInterval(() => {
        const from = ['claude', 'codex', 'cluadex'][i % 3];
        const to = ['codex', 'claude', 'claude'][i % 3];
        messages.push(mk(i, from, to, 'ทดสอบห้อง — ข้อความที่ ' + i, { topic: 'demo' }));
        if (messages.length > 40) messages.shift();
        apply({ agents, messages: messages.slice(), decisions });
        i++;
    }, 4200);
}

// ── main loop ───────────────────────────────────────────────────────

function frame() {
    T++;
    drawRoom();     // sprites, onto the logical canvas
    present();      // blitted up, then lit, at device resolution
    scanlines();
    drawOverlay();
    requestAnimationFrame(frame);
}

window.addEventListener('resize', resize);
window.addEventListener('message', onMessage);
try { window.chrome?.webview?.addEventListener('message', onMessage); } catch { /* not hosted */ }

resize();
requestAnimationFrame(frame);
post({ type: 'officeReady' });
if (new URLSearchParams(location.search).has('demo')) demo();
