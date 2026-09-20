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
/** Logical pixels per screen pixel. Two, not four: the painted room has a
 *  far finer scale than the one this file used to draw, so the sprite grid
 *  has to get finer with it or every character stands a head above the
 *  furniture. */
const SCALE = 2;
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
const ctx = scene.getContext('2d');                   // logical, sprites only, TRANSPARENT
const overlay = document.getElementById('overlay');

/** Lamps and screens, collected while the scene draws, lit in the second pass.
 *  Gathered rather than hardcoded so a desk that moves takes its light with it. */
let LIGHTS = [];

/**
 * The room itself, as a painted plate.
 *
 * Everything before this drew the office out of rectangles - floor tiles,
 * wall planes, cubicle panels, props - and got about as far as that approach
 * goes: a diagram with lighting on it. The owner supplied a proper isometric
 * interior instead, so the room is ART now and this file's job narrows to the
 * half it is actually good at: who is in the room, where, doing what, and
 * what is lit.
 *
 * STATIONS are the bridge. The plate has fixed furniture, so an agent cannot
 * stand anywhere - it has to stand at a desk that exists in the picture.
 * Normalised to the plate, so they survive any canvas size.
 */
const ROOM_PLATE = new Image();
let PLATE_READY = false;
ROOM_PLATE.onload = () => { PLATE_READY = true; };
ROOM_PLATE.src = 'art/room.webp';

const STATIONS = [
    // Measured off THIS plate, not off the reference render. The reference
    // has a big table in the middle of the room; this plate has a rug there
    // and its desks are all around the edges, so stations copied from the
    // other picture put four people standing on a carpet.
    { x: 0.800, y: 0.620, r: 34, warm: true },   // desk by the bookshelf, right
    { x: 0.165, y: 0.235, r: 30, warm: true },   // cabinets, back left
    { x: 0.470, y: 0.185, r: 30, warm: false },  // in front of the server racks
    { x: 0.700, y: 0.265, r: 28, warm: false },  // the glass meeting room
    { x: 0.205, y: 0.580, r: 30, warm: true },   // standing at the coffee bar
    { x: 0.430, y: 0.755, r: 28, warm: true },   // the sofa
    { x: 0.500, y: 0.520, r: 26, warm: true },   // on the rug, middle of the room
    { x: 0.880, y: 0.430, r: 26, warm: true },   // by the whiteboard, far right
];


/** Plate coordinates -> logical canvas pixels. The plate is drawn to COVER,
 *  so the same transform has to serve the art and everything placed on it,
 *  or the characters drift off their desks as the window changes. */
let PLATE_FIT = { x: 0, y: 0, w: 1, h: 1 };

function plateFit() {
    const iw = ROOM_PLATE.naturalWidth || 1448, ih = ROOM_PLATE.naturalHeight || 1086;
    // `min`, not `max`: COVER crops the room to fill the canvas, and this
    // plate is the subject rather than a background texture — losing the
    // coffee bar off one edge to avoid letterboxing is a bad trade.
    const k = Math.min(CW / iw, CH / ih);
    PLATE_FIT = { x: (CW - iw * k) / 2, y: (CH - ih * k) / 2, w: iw * k, h: ih * k };
    return PLATE_FIT;
}

const stationPt = (st) => ({
    x: PLATE_FIT.x + st.x * PLATE_FIT.w,
    y: PLATE_FIT.y + st.y * PLATE_FIT.h,
});

/** The darkness, with holes cut in it. See castDarkness(). */
const shadowLayer = document.createElement('canvas');
const shctx = shadowLayer.getContext('2d');

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
    shadowLayer.width = cv.width;
    shadowLayer.height = cv.height;
    ctx.imageSmoothingEnabled = false;
    layoutDesks();
    bossRelayout();
}

/**
 * Keep the boss where he was when the window changes size.
 *
 * He walks in LOGICAL canvas pixels, and those move under him on every resize:
 * without this he keeps his old coordinates and ends up outside the room, or
 * walking to a sofa that is now somewhere else. His position is converted to
 * plate-normalised space and back, which is the same space the furniture is
 * pinned in — so "in front of the sofa" survives any window.
 */
function bossRelayout() {
    if (!BOSS_AV) return;
    plateFit();
    const prev = BOSS_AV._norm;
    const p = prev
        ? { x: PLATE_FIT.x + prev.x * PLATE_FIT.w, y: PLATE_FIT.y + prev.y * PLATE_FIT.h }
        : spotPt(BOSS_HOME);
    BOSS_AV.x = p.x; BOSS_AV.y = p.y;
    // A trip in flight is abandoned rather than recomputed: the target was in
    // the old coordinate space, and walking to it would cross the new room.
    BOSS_AV.target = null;
    BOSS_PATH = null;
    if (BOSS_PLAN && BOSS_PLAN.phase === 'walking')
        BOSS_PLAN = { spot: BOSS_PLAN.spot, phase: 'resting', until: performance.now() + 1200 };
    // Logical scale, so the pack's nominalSpeed keeps his feet in step with
    // bossSpeed(); the draw call passes the device-resolution scale instead.
    BOSS_AV.scale = bossScaleLogical();
    BOSS_AV.speed = bossSpeed();
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
let BROKER = null;      // {state,tail} — the boss: running / adopted / stopped / failed
const DESKS = new Map();  // agent id → {gx,gy,seat:{x,y},screen:{x,y}}
const SEEN = new Set();   // message ids already shown as bubbles
const EMOTES_PLAYED = new Set();  // agent|atUtc, so one emote sounds once
let PRIMED = false;       // first payload is backlog: show it, don't perform it
const BUBBLES = [];       // {agent,text,color,until}
const PACKETS = [];       // {from,to,color,t0,ms}
let BOSS = null;          // {until,text} — the owner's last line, as a bubble

/* ── the boss, as painted animation ──────────────────────────────────
 *
 * Owner (2026-09-20): "เขาทำอนิเมชั่นบอสไว้แล้ว เอาเข้าห้องได้เลย".
 *
 * The BrainX Avatar Game Pack: 40 animations, 187 frames of 256×256 with a
 * shared foot anchor, on three atlases. Everybody else in this room is a
 * dozen rectangles this file draws, and that was the right call for a figure
 * the size of a thumbnail — but the boss is one character, in the middle of
 * the room, that the owner is looking straight at, and hand-placed pixels do
 * not animate a walk cycle.
 *
 * Drawn at DEVICE resolution rather than on the sprite grid, for the same
 * reason the room plate is: the art is 256 pixels tall and the logical canvas
 * is half the window. Routing it through the small canvas would throw most of
 * it away before scaling it back up.
 */
let BOSS_AV = null;       // BrainXAvatar, once its atlases have loaded
let BOSS_CLOCK = 0;       // performance.now() of the last avatar update

/**
 * Places in the room worth walking to, normalised to the plate.
 *
 * Owner (2026-09-20): "สามารถเดินไปเดินมาได้เอง ... นั่งบนโซฟาได้ (มีท่านั่ง)
 * หรือไปเคาน์เตอร์ กินน้ำได้".
 *
 * Each spot is a piece of the painted furniture plus what a person does when
 * they get there — the pack has a sit, a drink and a read, so the sofa, the
 * coffee bar and the bookshelf are real destinations rather than coordinates
 * to stand on. `stay` is how long he lingers, in seconds, picked at random in
 * that range so two visits never feel like a loop.
 */
const BOSS_SPOTS = SPOTS;                                  // roommap.js
const BOSS_HOME = SPOTS.find(s => s.home) || SPOTS[0];     // the rug, middle of the room

/** The route he is walking, as normalised plate points, and how far along it
 *  he is. A path exists because the room is a ring of furniture: a straight
 *  line from the coffee bar to the bookshelf goes through the sofa, and the
 *  grid in roommap.js is what knows that. */
let BOSS_PATH = null;
let BOSS_LEG = 0;

/** What he is doing with himself: {spot, phase, until}. `phase` is 'walking'
 *  (on his way), 'doing' (at the spot, mid-activity) or 'resting' (settled,
 *  waiting for `until` before choosing somewhere else). Null until the pack
 *  has loaded, because there is nobody to move. */
let BOSS_PLAN = null;

/** Set while the owner is speaking: he drops what he is doing, comes back to
 *  the rug and gives the order. Wandering resumes when the line expires. */
let BOSS_SUMMONED = false;

/** Height of the boss as a fraction of the room's height, tuned against the
 *  seated agents so he reads as a person in the same room rather than a
 *  cut-out pasted over it. 217 is the drawn height inside the 256px frame. */
const BOSS_FILL = 0.155;
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
/**
 * Give every agent a place in the painted room.
 *
 * The plate has fixed furniture, so this is an ASSIGNMENT rather than a
 * layout: the first agent takes the best seat and the rest fill outward in a
 * stable order, so nobody's chair moves when somebody else connects.
 */
function layoutDesks() {
    plateFit();
    DESKS.clear();
    AGENTS.forEach((a, i) => {
        const st = STATIONS[i % STATIONS.length];
        const pt = stationPt(st);
        DESKS.set(a.id, { st, desk: pt, screen: { x: pt.x, y: pt.y - 16 } });
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

/**
 * A floor tile with a SURFACE.
 *
 * The room was two navies in a checkerboard, which is a grid rather than a
 * floor: nothing caught light, nothing had an edge, and every tile was the
 * same as every other. Three things fix that, and none of them is a new
 * colour — a grout line so tiles have edges, a lit top-left bevel so the
 * light has a direction, and a per-tile value wobble so the surface has
 * grain instead of a repeat.
 */
function floorTile(gx, gy) {
    const p = iso(gx, gy);
    // Deterministic per-cell noise. Random would shimmer on every frame.
    const n = ((gx * 73856093) ^ (gy * 19349663)) >>> 0;
    const wob = ((n >>> 3) % 3) - 1;                 // -1, 0 or 1
    const base = (gx + gy) % 2 ? 22 : 16;
    const v = Math.max(10, base + wob * 2);
    const fill = `rgb(${(v * 0.72) | 0},${(v * 0.86) | 0},${(v * 2.0) | 0})`;

    ctx.fillStyle = fill;
    ctx.beginPath();
    ctx.moveTo(p.x, p.y);
    ctx.lineTo(p.x + TILE_W / 2, p.y + TILE_H / 2);
    ctx.lineTo(p.x, p.y + TILE_H);
    ctx.lineTo(p.x - TILE_W / 2, p.y + TILE_H / 2);
    ctx.closePath();
    ctx.fill();

    // Grout along the two far edges only — a full outline turns a floor into
    // graph paper, and light comes from the back-right of this room.
    ctx.strokeStyle = 'rgba(4,5,14,0.55)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(p.x - TILE_W / 2, p.y + TILE_H / 2);
    ctx.lineTo(p.x, p.y + TILE_H);
    ctx.lineTo(p.x + TILE_W / 2, p.y + TILE_H / 2);
    ctx.stroke();

    // And a lit bevel along the near-top edges.
    ctx.strokeStyle = 'rgba(150,180,255,0.07)';
    ctx.beginPath();
    ctx.moveTo(p.x - TILE_W / 2, p.y + TILE_H / 2);
    ctx.lineTo(p.x, p.y);
    ctx.lineTo(p.x + TILE_W / 2, p.y + TILE_H / 2);
    ctx.stroke();
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
    ctx.clearRect(0, 0, CW, CH);   // sprites only; the plate is drawn beneath
    plateFit();

    // Each occupied station contributes its light, which is what the darkness
    // pass cuts holes with. A station nobody is at stays dark - that is the
    // whole reason this is a list and not a constant.
    for (const a of AGENTS) {
        const d = DESKS.get(a.id);
        if (!d || a.state === 'offline') continue;
        LIGHTS.push({
            x: d.desk.x, y: d.desk.y - 8,
            r: d.st.r,
            c: d.st.warm ? [255, 186, 110] : hexToRgb(agentColor(a.id)),
            i: a.state === 'working' ? 0.50 : 0.34,
        });
    }

    // Back to front, so somebody nearer the viewer covers whoever is behind.
    const order = [...AGENTS].sort((x, y) => DESKS.get(x.id).desk.y - DESKS.get(y.id).desk.y);
    for (const a of order) if (a.state !== 'offline' && !VISITS.has(a.id)) drawSeated(a);
    for (const a of order) if (!a.bridge) drawWalker(a);

    // The boss stands in his own light, always — he is in the room whether or
    // not he has just spoken, and an unlit figure in the middle of the rug
    // would read as somebody who left. Brighter while he is talking.
    {
        const p = bossSpot();
        const talking = BOSS && T < BOSS.until;
        LIGHTS.push({
            x: p.x, y: p.y - 10,
            r: 30,
            c: hexToRgb(FIXED.owner),
            i: talking ? 0.52 : 0.30,
        });
    }

    // Only while the pack is still loading: the painted boss is drawn at
    // device resolution in present(), not here on the sprite grid.
    if (!BOSS_AV && BOSS && T < BOSS.until) drawBoss();
    drawPackets();
}

/** Somebody at their station. The desk, the chair and the monitor are all in
 *  the plate already - this draws the person and nothing else. */
function drawSeated(a) {
    const d = DESKS.get(a.id);
    if (!d) return;
    castShadow(d.desk.x, d.desk.y + 5, 9);
    // Owner (2026-09-20): "unreal unity เป็นโปรแกรม ไม่ใช่เอไอ ต้องถูกวางเป็น
    // เครื่องมือ ต้องแยกให้ถูก ... ที่ตอบเราได้และกำหนดเป็นตัวละครอวาต้าได้ ก็มี
    // ไม่กี่เจ้า". A bridge cannot answer, cannot be asked, and cannot pick a
    // face — drawing it as a colleague made the room claim six people when
    // four of them could talk. It gets the desk and the status light; the
    // chair stays empty.
    if (a.bridge) drawRig(d.desk.x, d.desk.y, a);
    else drawPerson(d.desk.x, d.desk.y, agentColor(a.id), a);
}

/* The engines, as equipment.
 *
 * A tower with a status lamp and a vent stack. Deliberately the same visual
 * weight as a seated figure so the room stays balanced, and deliberately
 * nothing like one: no head, no skin, no idle bob. It is lit when the bridge
 * has published a fresh heartbeat, dark when it has not, and the drive light
 * flickers only while a tool call is actually running through it. */
const RIG_ROWS = [
    '..8888888..',
    '.899999998.',
    '.897777798.',
    '.89gxxxg98.',
    '.897777798.',
    '.89-----98.',
    '.897777798.',
    '.89vvvvv98.',
    '.89vvvvv98.',
    '.897777798.',
    '.89-----98.',
    '.897777798.',
    '.8999999 8.',
    '..8888888..',
];

function drawRig(x, y, a) {
    const on = a.state !== 'offline';
    const busy = a.state === 'working';
    const c = agentColor(a.id);
    const body = on ? '#2b3242' : '#20242e';
    const pal = {
        '7': body,
        '8': shade(body, -0.45),
        '9': shade(body, 0.18),
        '-': shade(body, -0.25),
        'v': shade(body, -0.32),
        'x': on ? '#0d1016' : '#0b0d12',
        // The status lamp is the agent's own colour, so a glance at the room
        // tells you WHICH engine is up without reading a nameplate.
        'g': on ? (busy && (T >> 3) % 2 ? shade(c, 0.35) : c) : '#39405a',
    };
    drawGrid(RIG_ROWS, Math.round(x) - 5, Math.round(y) - 13, pal);

    // Its light belongs in the room's lighting pass like everybody else's —
    // an unlit corner with a running machine in it reads as an empty corner.
    if (on) LIGHTS.push({ x, y: y - 6, r: 16, c: hexToRgb(c), i: busy ? 0.34 : 0.20 });
}


/**
 * Shadow in the corners.
 *
 * A room lit evenly everywhere has no corners — every surface reads at the
 * same distance and the whole thing flattens into a pattern. Darkening where
 * the floor meets each wall is the cheapest depth cue there is, and it is the
 * single biggest reason the first version looked like a diagram.
 */
function floorAO() {
    const A = iso(0, ROOM.R), B = iso(0, 0), C = iso(ROOM.C, 0);
    const band = (p1, p2, depth) => {
        const g = ctx.createLinearGradient(
            (p1.x + p2.x) / 2, (p1.y + p2.y) / 2,
            (p1.x + p2.x) / 2 + depth.dx, (p1.y + p2.y) / 2 + depth.dy);
        g.addColorStop(0, 'rgba(3,4,12,0.55)');
        g.addColorStop(1, 'rgba(3,4,12,0)');
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.moveTo(p1.x, p1.y); ctx.lineTo(p2.x, p2.y);
        ctx.lineTo(p2.x + depth.dx, p2.y + depth.dy);
        ctx.lineTo(p1.x + depth.dx, p1.y + depth.dy);
        ctx.closePath(); ctx.fill();
    };
    band(A, B, { dx: TILE_W * 0.9, dy: TILE_H * 0.45 });    // along the left wall
    band(B, C, { dx: -TILE_W * 0.9, dy: TILE_H * 0.45 });   // along the right wall
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

    /** One wall plane: a vertical gradient, vertical panelling, and a rail.
     *
     *  Flat fills were the other half of why this read as a backdrop. A wall
     *  in a lit room is lighter where it faces the ceiling and darker at the
     *  skirting, and it has SOMETHING on it at a regular interval — panels,
     *  studs, a rail — or the eye has nothing to measure the room against. */
    const quad = (p1, p2, top, bottom, panel) => {
        const g = ctx.createLinearGradient(0, p1.y - H, 0, p1.y);
        g.addColorStop(0, top);
        g.addColorStop(1, bottom);
        ctx.save();
        ctx.beginPath();
        ctx.moveTo(p1.x, p1.y); ctx.lineTo(p2.x, p2.y);
        ctx.lineTo(p2.x, p2.y - H); ctx.lineTo(p1.x, p1.y - H);
        ctx.closePath();
        ctx.clip();
        ctx.fillStyle = g;
        ctx.fill();

        // Panelling: a lit seam every few cells, running with the wall.
        const steps = 10;
        ctx.strokeStyle = panel;
        ctx.lineWidth = 1;
        for (let i = 1; i < steps; i++) {
            const t = i / steps;
            const x = p1.x + (p2.x - p1.x) * t, y = p1.y + (p2.y - p1.y) * t;
            ctx.beginPath();
            ctx.moveTo(x, y); ctx.lineTo(x, y - H + 4);
            ctx.stroke();
        }

        // A dado rail two thirds up, which is what gives the wall a height
        // the eye can actually read.
        ctx.strokeStyle = 'rgba(8,10,24,0.55)';
        ctx.beginPath();
        ctx.moveTo(p1.x, p1.y - H * 0.62); ctx.lineTo(p2.x, p2.y - H * 0.62);
        ctx.stroke();
        ctx.strokeStyle = 'rgba(160,190,255,0.10)';
        ctx.beginPath();
        ctx.moveTo(p1.x, p1.y - H * 0.62 - 1); ctx.lineTo(p2.x, p2.y - H * 0.62 - 1);
        ctx.stroke();
        ctx.restore();
    };
    // Clearly lighter than the floor or the room has no back, and the two
    // walls clearly different from each other or the corner disappears.
    // Clearly lighter than the floor, and clearly different from each other,
    // or the corner between them disappears.
    quad(a, b, '#2b3260', '#191e40', 'rgba(150,180,255,0.05)');
    quad(b, c, '#3c4585', '#232a58', 'rgba(170,200,255,0.08)');

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

/**
 * The cubicle: two fabric panels meeting behind the desk.
 *
 * Owner: "ควรเป็นที่ทำงาน มี พาติชั่นกั้นชัดเจน แบบมืออาชีพ". This is the
 * thing that turns desks-on-a-floor into an office. It also does real work
 * for the picture: each panel is a large flat plane at a known angle, so the
 * room finally has surfaces catching light at two different orientations
 * instead of one floor and two distant walls.
 *
 * Drawn BEFORE the desk and whoever is at it, because the panels stand behind
 * them — an office divider in front of the person would read as a fence.
 */
function drawCubicle(cx, cy, dim) {
    const PW = TILE_W * 1.05, PD = TILE_H * 1.05, H = 26;

    // The tile's corners, relative to its centre.
    const left = { x: cx - PW / 2, y: cy };
    const back = { x: cx, y: cy - PD / 2 };
    const right = { x: cx + PW / 2, y: cy };

    const panel = (p1, p2, face, lip, rail) => {
        ctx.fillStyle = face;
        ctx.beginPath();
        ctx.moveTo(p1.x, p1.y); ctx.lineTo(p2.x, p2.y);
        ctx.lineTo(p2.x, p2.y - H); ctx.lineTo(p1.x, p1.y - H);
        ctx.closePath(); ctx.fill();

        // Fabric: faint horizontal weave. Two values, four pixels apart — any
        // more and it reads as corrugated metal.
        ctx.strokeStyle = lip;
        ctx.lineWidth = 1;
        for (let k = 4; k < H - 3; k += 4) {
            ctx.beginPath();
            ctx.moveTo(p1.x, p1.y - k); ctx.lineTo(p2.x, p2.y - k);
            ctx.stroke();
        }

        // The top rail, given thickness by drawing the same edge twice two
        // pixels apart. That lip is most of what makes a flat quad read as a
        // panel you could rest a coffee on.
        ctx.strokeStyle = rail;
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.moveTo(p1.x, p1.y - H + 1); ctx.lineTo(p2.x, p2.y - H + 1);
        ctx.stroke();
        ctx.strokeStyle = 'rgba(6,8,18,0.5)';
        ctx.lineWidth = 1;
        ctx.beginPath();
        ctx.moveTo(p1.x, p1.y - H + 3); ctx.lineTo(p2.x, p2.y - H + 3);
        ctx.stroke();
    };

    // Left panel faces away from the light, right panel toward it.
    panel(left, back, dim ? '#242a47' : '#333b63', 'rgba(10,12,28,0.30)', dim ? '#3b4470' : '#59639c');
    panel(back, right, dim ? '#2b3252' : '#3e4776', 'rgba(10,12,28,0.22)', dim ? '#454f80' : '#6b76b4');
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

    drawCubicle(x, y, off);
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
/** A spot's place on the LOGICAL canvas. Pinned to the plate, like the desks
 *  are: the old version derived a spot from the retired desk grid, which after
 *  the painted room arrived put the boss inside the furniture. */
function spotPt(s) {
    return {
        x: PLATE_FIT.x + s.x * PLATE_FIT.w,
        y: PLATE_FIT.y + s.y * PLATE_FIT.h,
    };
}

/** Where he actually IS — his own position once he is walking around, and his
 *  home spot before the pack has loaded. Speech bubbles follow this. */
function bossSpot() {
    if (BOSS_AV) return { x: BOSS_AV.x, y: BOSS_AV.y };
    return spotPt(BOSS_HOME);
}

/** Scale that puts the pack's 217px-tall figure at BOSS_FILL of the room, in
 *  DEVICE pixels — the space the avatar is actually drawn in. */
function bossScale() {
    return (PLATE_FIT.h * SCALE * BOSS_FILL) / 217;
}

/** The same height in LOGICAL pixels, which is the space he walks in. Both
 *  exist because the figure is drawn at device resolution but moves on the
 *  same grid as the desks — one number in two units, not two numbers. */
function bossScaleLogical() { return bossScale() / SCALE; }

/**
 * The boss, drawn from the pack, at device resolution.
 *
 * Called from present() between the sprite blit and the lighting pass, so he
 * is lit by the same darkness cut that everybody else is — draw him after the
 * lights and he would be a bright sticker on a dim room.
 */
function drawBossSprite() {
    if (!BOSS_AV) return;
    const now = performance.now();
    const dt = BOSS_CLOCK ? (now - BOSS_CLOCK) / 1000 : 0;
    BOSS_CLOCK = now;
    BOSS_AV.update(dt);
    bossTick();
    // Remember where he is in the room's own coordinates, so a resize can put
    // him back in front of the same sofa rather than at the same pixel.
    if (PLATE_FIT.w > 1 && PLATE_FIT.h > 1) BOSS_AV._norm = {
        x: (BOSS_AV.x - PLATE_FIT.x) / PLATE_FIT.w,
        y: (BOSS_AV.y - PLATE_FIT.y) / PLATE_FIT.h,
    };
    BOSS_AV.draw(vctx, {
        x: BOSS_AV.x * SCALE,
        y: BOSS_AV.y * SCALE,
        scale: bossScale(),
    });
}

/**
 * Furniture drawn back OVER the character, so he can stand behind things.
 *
 * Owner (2026-09-20): "ตรงไหนต้องบังตัวละคร บริเวณมุมบนซ้าย ก็ต้องคำนึง".
 *
 * There is no second set of art for this. Each occluder is a polygon on the
 * plate, and the piece inside it is re-drawn from the plate itself over the
 * sprite — a clip and one drawImage. That means the cut-outs can never drift
 * out of sync with the room, which a hand-exported foreground layer would do
 * the first time the plate is repainted.
 *
 * `base` is the piece's depth line: a character whose feet are above it is
 * further into the room and gets covered. One number per object is enough
 * because everything here stands on the same floor.
 */
/* Occluders, sorted the way a game sorts them.
 *
 * Owner (2026-09-20): "ตรงที่ต้องบัง นี่คือ ถ้าเราเดินเข้าด้านหลังจะบัง แต่ถ้า
 * เดินเข้าข้างหน้าจะไม่บัง จะทำไง เทคนี้ในเกมใช้เยอะด้วย".
 *
 * Exactly right, and the previous version could not do it. It re-drew every
 * occluder pixel BELOW the character's feet, which is true for a character
 * standing behind the sofa and false the moment they walk around to the front
 * of it: the sofa's own front legs are still below their feet, so the sofa
 * kept painting over somebody standing in front of it.
 *
 * The fix is the standard one — Y-SORT PER OBJECT, not per pixel:
 *
 *   every object gets a BASELINE, the screen row where it meets the floor.
 *   character's feet ABOVE that line  → character is behind → draw the object over them
 *   character's feet BELOW that line  → character is in front → draw nothing
 *
 * with one refinement that matters in an isometric room: the baseline is per
 * COLUMN, not one number for the whole object. A sofa photographed from a
 * corner meets the floor at a different height at its left end than at its
 * right, and a single baseline makes the character pop in front of it half a
 * step too early at one end and half a step too late at the other.
 *
 * So the mask is split into connected pieces once, each piece keeps its own
 * cut-out of the plate plus a baseline for every column it covers, and each
 * frame asks one question per piece: at the column where this character is
 * standing, is that piece's floor line below the character's feet?
 */
let OCC_PIECES = null;

function buildOccluderPieces() {
    const m = ROOM_MAP.mask;
    if (!m || !PLATE_READY) return null;

    const W = m.w, H = m.h;
    const on = new Uint8Array(W * H);
    for (let i = 0, p = 0; i < on.length; i++, p += 4) on[i] = m.data[p + 2] > 127 ? 1 : 0;

    // Connected components, iterative (a 1.5M-pixel region would blow a
    // recursive fill) and once per mask, not per frame.
    const lab = new Int32Array(W * H).fill(-1);
    const pieces = [];
    const stack = [];
    for (let seed = 0; seed < on.length; seed++) {
        if (!on[seed] || lab[seed] >= 0) continue;
        const id = pieces.length;
        let x0 = W, y0 = H, x1 = 0, y1 = 0, count = 0;
        stack.length = 0;
        stack.push(seed);
        lab[seed] = id;
        while (stack.length) {
            const i = stack.pop();
            const x = i % W, y = (i / W) | 0;
            count++;
            if (x < x0) x0 = x; if (x > x1) x1 = x;
            if (y < y0) y0 = y; if (y > y1) y1 = y;
            if (x > 0     && on[i - 1] && lab[i - 1] < 0) { lab[i - 1] = id; stack.push(i - 1); }
            if (x < W - 1 && on[i + 1] && lab[i + 1] < 0) { lab[i + 1] = id; stack.push(i + 1); }
            if (y > 0     && on[i - W] && lab[i - W] < 0) { lab[i - W] = id; stack.push(i - W); }
            if (y < H - 1 && on[i + W] && lab[i + W] < 0) { lab[i + W] = id; stack.push(i + W); }
        }
        // A handful of stray pixels is paint noise, not a piece of furniture.
        pieces.push(count < 120 ? null : { id, x0, y0, x1, y1, count });
    }

    const kept = [];
    for (const p of pieces) {
        if (!p) continue;
        const w = p.x1 - p.x0 + 1, h = p.y1 - p.y0 + 1;

        // The cut-out: this piece of the plate, and nothing else.
        const c = document.createElement('canvas');
        c.width = w; c.height = h;
        const cx = c.getContext('2d');
        cx.drawImage(ROOM_PLATE,
            p.x0 * (ROOM_PLATE.naturalWidth / W), p.y0 * (ROOM_PLATE.naturalHeight / H),
            w * (ROOM_PLATE.naturalWidth / W), h * (ROOM_PLATE.naturalHeight / H),
            0, 0, w, h);
        const img = cx.getImageData(0, 0, w, h);
        const base = new Int32Array(w).fill(-1);
        for (let y = 0; y < h; y++) {
            for (let x = 0; x < w; x++) {
                const gi = (p.y0 + y) * W + (p.x0 + x);
                if (lab[gi] === p.id) {
                    if (p.y0 + y > base[x]) base[x] = p.y0 + y;   // lowest row = floor line
                } else {
                    img.data[(y * w + x) * 4 + 3] = 0;            // not this piece: transparent
                }
            }
        }
        cx.putImageData(img, 0, 0);

        // Columns the piece does not cover fall back to its lowest point, so a
        // character standing in a gap still sorts sensibly against it.
        let lowest = 0;
        for (let x = 0; x < w; x++) if (base[x] > lowest) lowest = base[x];
        kept.push({ canvas: c, x0: p.x0, y0: p.y0, x1: p.x1, y1: p.y1, w, h, base, lowest, mw: W, mh: H });
    }
    console.info('cowork: occluders split into', kept.length, 'pieces');
    return kept;
}

function drawOccluders() {
    if (!PLATE_READY || !BOSS_AV) return;
    const n = bossNorm();

    // ── painted mask: the preferred path ──
    //
    // Everything in the occluder layer BELOW the character's feet is nearer
    // the viewer, because this is an isometric room drawn from a fixed camera
    // — so re-drawing exactly that band over the sprite is the whole depth
    // test, with no per-object base line to measure or get wrong. That is the
    // part the hand-placed polygons kept getting wrong: one number per piece,
    // guessed, for objects whose real depth varies along their own width.
    if (ROOM_MAP.mask) {
        if (!OCC_PIECES) OCC_PIECES = buildOccluderPieces();
        if (OCC_PIECES) {
            const f = PLATE_FIT;
            const s = bossScaleLogical();
            const mw = OCC_PIECES.length ? OCC_PIECES[0].mw : 1;
            const mh = OCC_PIECES.length ? OCC_PIECES[0].mh : 1;
            const feetX = n.x * mw, feetY = n.y * mh;
            // The character's own width in mask pixels, so a piece is only
            // considered when it actually overlaps them.
            const halfW = (34 * s / PLATE_FIT.w) * mw;

            // The sprite's own box in mask pixels. A piece that does not
            // overlap it cannot hide any of it, however the depths compare —
            // standing in the middle of the room was re-drawing the sofa over
            // empty air two pieces at a time.
            const headY = feetY - (220 * s / PLATE_FIT.h) * mh;
            for (const p of OCC_PIECES) {
                if (feetX + halfW < p.x0 || feetX - halfW > p.x1) continue;
                if (p.y1 < headY || p.y0 > feetY) continue;

                // The floor line of THIS piece, at the column this character
                // is standing in — averaged across their width so a one-pixel
                // notch in the outline cannot flip the decision.
                let base = -1, hits = 0;
                const from = Math.max(0, Math.round(feetX - halfW) - p.x0);
                const to = Math.min(p.w - 1, Math.round(feetX + halfW) - p.x0);
                for (let x = from; x <= to; x++) {
                    if (p.base[x] < 0) continue;
                    base += p.base[x]; hits++;
                }
                base = hits ? (base + 1) / hits : p.lowest;

                // Feet BELOW the line means the character is nearer the
                // viewer than this object, so it must not be drawn over them.
                if (feetY >= base) continue;

                vctx.imageSmoothingEnabled = true;
                vctx.imageSmoothingQuality = 'high';
                vctx.drawImage(p.canvas,
                    (f.x + (p.x0 / mw) * f.w) * SCALE,
                    (f.y + (p.y0 / mh) * f.h) * SCALE,
                    (p.w / mw) * f.w * SCALE,
                    (p.h / mh) * f.h * SCALE);
            }
            return;
        }
    }

    const f = PLATE_FIT;
    const s = bossScaleLogical();
    // The character's footprint on the plate, so a piece on the far side of
    // the room is skipped instead of being re-blitted every frame.
    const halfW = (34 * s) / f.w, height = (220 * s) / f.h;

    for (const o of ROOM_MAP.OCCLUDERS) {
        if (n.y >= o.base) continue;
        const bb = o._bb || (o._bb = polyBounds(o.poly));
        if (n.x + halfW < bb.x0 || n.x - halfW > bb.x1) continue;
        if (n.y < bb.y0 - height || n.y > bb.y1) continue;

        vctx.save();
        vctx.beginPath();
        for (let i = 0; i < o.poly.length; i++) {
            const X = (f.x + o.poly[i][0] * f.w) * SCALE;
            const Y = (f.y + o.poly[i][1] * f.h) * SCALE;
            if (i) vctx.lineTo(X, Y); else vctx.moveTo(X, Y);
        }
        vctx.closePath();
        vctx.clip();
        vctx.imageSmoothingEnabled = true;
        vctx.imageSmoothingQuality = 'high';
        vctx.drawImage(ROOM_PLATE, f.x * SCALE, f.y * SCALE, f.w * SCALE, f.h * SCALE);
        vctx.restore();
    }
}

function polyBounds(poly) {
    let x0 = 1, y0 = 1, x1 = 0, y1 = 0;
    for (const [x, y] of poly) {
        if (x < x0) x0 = x; if (x > x1) x1 = x;
        if (y < y0) y0 = y; if (y > y1) y1 = y;
    }
    return { x0, y0, x1, y1 };
}

// ── the boss, walking around a room he lives in ─────────────────────

/** Walking pace, in logical pixels per second, tied to the room rather than
 *  fixed: the same number crosses a small window in a hurry and a large one at
 *  a crawl. `nominalSpeed` in the manifest then keeps his feet in step with it. */
function bossSpeed() { return Math.max(18, PLATE_FIT.w * 0.075); }

/** Put him somewhere without animating the trip — used on load and on resize,
 *  where the room changed size under him and a walk would be a lie. */
function bossPlace(s) {
    if (!BOSS_AV) return;
    const p = spotPt(s);
    BOSS_AV.x = p.x; BOSS_AV.y = p.y;
    BOSS_AV.target = null;
}

/**
 * Send him to a spot.
 *
 * Two details that are not decoration:
 *  - Standing up first. `posture` is seated after sit_down, and walking
 *    straight out of a chair plays a walk cycle of somebody sitting down.
 *  - The rug as a waypoint. The room is a ring of furniture around an open
 *    middle, so a straight line from the coffee bar to the bookshelf goes
 *    through the sofa. Anything crossing the room routes through the middle,
 *    which is both shorter to compute than real pathfinding and closer to how
 *    a person actually crosses a room.
 */
function bossGoTo(spot) {
    if (!BOSS_AV) return;

    if (BOSS_AV.posture === 'seated') {
        BOSS_AV.play('stand_up', { loop: false });
        // Back onto the floor he sat down from, so the path that follows
        // starts somewhere the grid says a person can be. Standing up out of
        // the cushion's coordinates would begin the route inside the sofa.
        const from = BOSS_PLAN?.spot;
        if (from?.seat) {
            BOSS_AV.x = PLATE_FIT.x + from.x * PLATE_FIT.w;
            BOSS_AV.y = PLATE_FIT.y + from.y * PLATE_FIT.h;
        }
        BOSS_PLAN = { spot, phase: 'rising', until: performance.now() + 880 };
        return;
    }

    const from = bossNorm();
    const path = ROOM_MAP.findPath(from, { x: spot.x, y: spot.y });
    if (!path.length) {
        // Nowhere to walk — the target is walled off, or he is already there.
        BOSS_PLAN = { spot, phase: 'resting', until: performance.now() + 3000 };
        return;
    }
    BOSS_PATH = path;
    BOSS_LEG = 0;
    bossWalkLeg();
    BOSS_PLAN = { spot, phase: 'walking', until: 0 };
}

/** Where he is, in the plate's own coordinates. */
function bossNorm() {
    // Computed from where he is right now, not from the cached `_norm`: that
    // one is a snapshot taken while drawing, and planning a route from a
    // stale position sends him walking from somewhere he already left.
    if (BOSS_AV && PLATE_FIT.w > 1 && PLATE_FIT.h > 1) return {
        x: (BOSS_AV.x - PLATE_FIT.x) / PLATE_FIT.w,
        y: (BOSS_AV.y - PLATE_FIT.y) / PLATE_FIT.h,
    };
    return { x: BOSS_HOME.x, y: BOSS_HOME.y };
}

/** Start the next straight leg of the route. */
function bossWalkLeg() {
    const leg = BOSS_PATH?.[BOSS_LEG];
    if (!leg) return false;
    const p = { x: PLATE_FIT.x + leg.x * PLATE_FIT.w, y: PLATE_FIT.y + leg.y * PLATE_FIT.h };
    BOSS_AV.moveTo(p.x, p.y, { speed: bossSpeed() });
    return true;
}

/** What he does once he is there. Every one of these returns to idle on its
 *  own through the manifest's `next`, except the sit, which is meant to last. */
function bossArrive(spot) {
    const secs = spot.stay[0] + Math.random() * (spot.stay[1] - spot.stay[0]);
    // Face the room rather than the wall he just walked at. The pack picks a
    // direction from the walk it finished, which for the coffee bar means
    // standing with his back to everybody.
    if (spot.face) BOSS_AV.direction = spot.face;
    switch (spot.act) {
        case 'sit':
            BOSS_AV.play('sit_down', { loop: false });
            // The walk ended on the floor in front of the cushion; the sitting
            // frames belong ON it. Moved once, here, rather than making the
            // cushion a walk target the pathfinder can never reach.
            if (spot.seat) {
                BOSS_AV.x = PLATE_FIT.x + spot.seat.x * PLATE_FIT.w;
                BOSS_AV.y = PLATE_FIT.y + spot.seat.y * PLATE_FIT.h;
            }
            break;
        case 'drink': BOSS_AV.play('drink', { loop: false }); break;
        case 'read':  BOSS_AV.play('read'); break;
        case 'phone': BOSS_AV.play('phone'); break;
        case 'point': BOSS_AV.play('point', { loop: false }); break;
        case 'think': BOSS_AV.play('think'); break;
        default:      BOSS_AV.stop();
    }
    BOSS_PLAN = { spot, phase: 'resting', until: performance.now() + secs * 1000 };
}

/** Somewhere else to be — never the place he is already standing. */
function bossPickSpot() {
    const here = BOSS_PLAN?.spot?.key;
    const options = BOSS_SPOTS.filter(s => s.key !== here);
    return options[Math.floor(Math.random() * options.length)] || BOSS_HOME;
}

/**
 * One step of the boss's own life, run every frame.
 *
 * Deliberately a poll rather than callbacks on the pack's onComplete: a click
 * from the owner can interrupt any of these at any moment, and a state machine
 * that reads "where am I, what changed" survives that, while a chain of
 * completion handlers ends up firing for a trip that was abandoned.
 */
function bossTick() {
    if (!BOSS_AV) return;
    const now = performance.now();

    // Lights out: he goes home and sleeps there. `sleep` is in the pack — this
    // is the one thing it was always for.
    if (!ROOM_OPEN) {
        if (BOSS_ASLEEP) return;                 // already out; nothing moves him

        // Walk him back first, so he is not asleep standing in the middle of
        // the floor. Once he is home (or was already), he lies down.
        const home = BOSS_PLAN?.spot === BOSS_HOME && !BOSS_AV.target;
        if (!home && BOSS_PLAN?.phase !== 'walking' && BOSS_PLAN?.phase !== 'rising') {
            bossGoTo(BOSS_HOME);
            return;
        }
        if (BOSS_AV.target) return;              // still on his way

        BOSS_ASLEEP = true;
        BOSS_PLAN = { spot: BOSS_HOME, phase: 'resting', until: Infinity };
        try { BOSS_AV.play('sleep'); } catch { BOSS_AV.play('idle'); }
        return;
    }

    // Lights back on, and he is the first one up.
    if (BOSS_ASLEEP) {
        BOSS_ASLEEP = false;
        BOSS_AV.play('stand_up', { loop: false });
        BOSS_PLAN = { spot: BOSS_HOME, phase: 'rising', until: now + 880 };
        return;
    }

    // The owner talking outranks whatever he was doing. He comes back to the
    // rug to say it, because an order shouted from the coffee bar reads as
    // somebody muttering into a cup.
    const talking = BOSS && T < BOSS.until;
    if (talking && !BOSS_SUMMONED) {
        BOSS_SUMMONED = true;
        bossGoTo(BOSS_HOME, { viaMiddle: false });
        return;
    }
    if (!talking && BOSS_SUMMONED) {
        BOSS_SUMMONED = false;
        BOSS_PLAN = { spot: BOSS_HOME, phase: 'resting', until: now + 4000 };
    }

    if (!BOSS_PLAN) { BOSS_PLAN = { spot: BOSS_HOME, phase: 'resting', until: now + 3000 }; return; }

    switch (BOSS_PLAN.phase) {
        case 'rising':
            // stand_up is 880ms and the pack sends him to idle after it.
            if (now >= BOSS_PLAN.until) bossGoTo(BOSS_PLAN.spot);
            break;
        case 'walking':
            // moveTo clears `target` the moment a leg is finished. The route
            // came from the grid, so each leg is a straight line that stays
            // off the furniture; walking it one leg at a time is what keeps
            // him out of the sofa without any per-frame collision check.
            if (!BOSS_AV.target) {
                BOSS_LEG++;
                if (!bossWalkLeg()) { BOSS_PATH = null; bossArrive(BOSS_PLAN.spot); }
            }
            break;
        case 'reacting':
            if (now < BOSS_PLAN.until) break;
            if (BOSS_PLAN.resume) {
                BOSS_AV.moveTo(BOSS_PLAN.resume.x, BOSS_PLAN.resume.y, { speed: bossSpeed() });
                BOSS_PLAN = { spot: BOSS_PLAN.spot, phase: 'walking', until: 0 };
            } else {
                BOSS_PLAN = { spot: BOSS_PLAN.spot, phase: 'resting', until: now + 4000 };
            }
            break;
        case 'resting':
            if (talking || BOSS_SUMMONED) break;
            if (now >= BOSS_PLAN.until) bossGoTo(bossPickSpot());
            break;
    }
}

// ── the owner can poke him ──────────────────────────────────────────

/** Is this logical-canvas point on the boss? A box around the drawn figure,
 *  which is 105 wide and 217 tall inside its frame, with a little slack so a
 *  click near his feet still counts. */
function bossHit(lx, ly) {
    if (!BOSS_AV) return false;
    const s = bossScaleLogical();
    return Math.abs(lx - BOSS_AV.x) <= 34 * s && ly <= BOSS_AV.y + 8 * s && ly >= BOSS_AV.y - 220 * s;
}

/** Clicked ON him: he reacts with one of the pack's one-shot gestures and a
 *  sound. */
const BOSS_REACTIONS = ['wave', 'laugh', 'joy', 'celebrate', 'agree', 'surprised', 'love'];

function bossPoke() {
    if (!BOSS_AV) return;
    if (BOSS_AV.posture === 'seated') {
        // Poked in his chair: he gets up rather than miming a wave sitting down.
        BOSS_AV.play('stand_up', { loop: false });
        BOSS_PLAN = { spot: BOSS_PLAN?.spot || BOSS_HOME, phase: 'rising', until: performance.now() + 880 };
        playSound('ok');
        return;
    }
    // Poked mid-walk, the trip has to be remembered. play() clears `target`
    // for anything that is not a walk cycle — which is right, a person does
    // not keep sliding across the room while they wave — so the destination is
    // kept here and he carries on once the gesture finishes.
    const resume = BOSS_AV.target ? { x: BOSS_AV.target.x, y: BOSS_AV.target.y } : null;
    const pick = BOSS_REACTIONS[Math.floor(Math.random() * BOSS_REACTIONS.length)];
    BOSS_AV.play(pick, { loop: false });
    playSound(pick === 'celebrate' || pick === 'joy' ? 'levelup' : 'ok');
    BOSS_PLAN = {
        spot: BOSS_PLAN?.spot || BOSS_HOME,
        phase: 'reacting',
        until: performance.now() + 920,
        resume,
    };
}

/**
 * Clicked somewhere else: he goes there.
 *
 * A click that lands near a piece of furniture becomes a visit to THAT
 * furniture — so clicking the sofa sits him down and clicking the coffee bar
 * gets him a drink, which is what a person means when they click a sofa.
 * Anywhere else is a plain walk to the spot on the floor.
 */
function bossSendTo(lx, ly) {
    if (!BOSS_AV) return;
    const nx = (lx - PLATE_FIT.x) / PLATE_FIT.w;
    const ny = (ly - PLATE_FIT.y) / PLATE_FIT.h;

    // Clicked a piece of furniture that means something: go and use it.
    const spot = ROOM_MAP.spotNear(nx, ny);
    if (spot) { bossGoTo(spot); return; }

    // Clicked the floor. A click on the sofa, a wall or off the plate lands on
    // the nearest place a person could stand instead of being ignored — the
    // room should never look like it did not hear you.
    const target = ROOM_MAP.nearestWalkable(nx, ny);
    if (!target) return;
    bossGoTo({ key: 'floor', x: target.x, y: target.y, act: 'idle', stay: [5, 12] });
}

function onRoomClick(e) {
    // Asleep is asleep. The light is the only thing that wakes him.
    if (!ROOM_OPEN) return;
    const r = cv.getBoundingClientRect();
    if (!r.width || !r.height) return;
    // Client px → logical canvas px. The canvas is CSS-scaled, so the ratio is
    // the only safe conversion; cv.width would be device pixels.
    const lx = (e.clientX - r.left) / r.width * CW;
    const ly = (e.clientY - r.top) / r.height * CH;
    if (bossHit(lx, ly)) bossPoke();
    else bossSendTo(lx, ly);
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

    // The plate is ART, not part of the sprite grid, so it is drawn straight
    // onto the visible canvas at full device resolution. Routing it through
    // the logical canvas first would throw away most of what makes it good.
    vctx.fillStyle = '#080a16';
    vctx.fillRect(0, 0, W, H);
    if (PLATE_READY) {
        vctx.imageSmoothingEnabled = true;
        vctx.imageSmoothingQuality = 'high';
        const f = PLATE_FIT;
        vctx.drawImage(ROOM_PLATE, f.x * SCALE, f.y * SCALE, f.w * SCALE, f.h * SCALE);
    }

    // Sprites on top, blown up with smoothing off so they stay crisp.
    vctx.imageSmoothingEnabled = false;
    vctx.drawImage(scene, 0, 0, W, H);

    // The boss, at his own resolution, before the light is cut — so the room's
    // darkness falls on him the same way it falls on everybody else.
    drawBossSprite();
    // …and whatever he is standing behind, painted back over him.
    drawOccluders();
    if (MAP_DEBUG) drawMapDebug();

    castDarkness();

    // Bloom. `lighter` so overlapping lamps build up rather than flatten each
    // other, and a soft radial falloff so the pixels underneath stay readable
    // through it instead of being washed out.
    vctx.globalCompositeOperation = 'lighter';
    for (const L of (ROOM_OPEN ? LIGHTS : [])) {
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

/**
 * The office is DARK, and each working agent lights their own corner of it.
 *
 * Owner: "เราทำเป็น เกม พวก เกม ดังเจี้ยน ได้ไหม แต่เป็นห้องทำงานฉากแบบนั้น".
 * This is the whole look of a dungeon crawler in one idea — the room exists,
 * but you only see the parts something is lighting — and here it costs
 * nothing to make honest, because the light sources ARE the live agents. A
 * cubicle whose owner went offline sinks into the dark. An empty office is a
 * dark office. Nothing has to be invented for that to be true.
 *
 * Built as a separate layer: fill it with darkness, cut holes with
 * destination-out, then lay the whole thing over the scene. Darkening the
 * visible canvas directly and then trying to lighten it back would crush the
 * pixels first and recover a grey smear.
 */
function castDarkness() {
    const W = cv.width, H = cv.height;
    shctx.globalCompositeOperation = 'source-over';
    shctx.clearRect(0, 0, W, H);

    // Never pitch black: a room you cannot see at all is not atmospheric, it
    // is broken. This much still reads as "unlit" while leaving the furniture
    // legible enough to know it is there.
    //
    // With the room CLOSED the blanket goes to full weight and no holes are
    // cut, because that is what the lamps being off looks like. Not quite
    // opaque: the owner should still see the shape of their office, and the
    // boss asleep in it.
    shctx.fillStyle = ROOM_OPEN ? 'rgba(3,4,12,0.80)' : 'rgba(2,3,9,0.93)';
    shctx.fillRect(0, 0, W, H);

    // Cut a hole per light. Two stops with a wide soft tail, because a hard
    // edge reads as a spotlight rather than as a lamp in a room.
    shctx.globalCompositeOperation = 'destination-out';

    // With everything off, one pool over the sleeper. Not a lamp — the point
    // is that you can still see him asleep; a dark room you cannot find
    // anybody in is the black rectangle this function was written to avoid.
    const nightLight = (!ROOM_OPEN && BOSS_AV)
        ? [{ x: BOSS_AV.x, y: BOSS_AV.y - 24, r: 38, c: [150, 170, 255], i: 0.2 }]
        : [];

    for (const L of (ROOM_OPEN ? LIGHTS : nightLight)) {
        const cx = L.x * SCALE, cy = L.y * SCALE, r = L.r * SCALE * 1.9;
        const g = shctx.createRadialGradient(cx, cy, r * 0.10, cx, cy, r);
        g.addColorStop(0, 'rgba(0,0,0,1)');
        g.addColorStop(0.45, 'rgba(0,0,0,0.72)');
        g.addColorStop(1, 'rgba(0,0,0,0)');
        shctx.fillStyle = g;
        shctx.fillRect(cx - r, cy - r, r * 2, r * 2);
    }

    // And a permanent dim glow over the middle of the floor, so an office with
    // nobody in it is still a room and not a black rectangle.
    const cx = W / 2, cy = H * 0.52, r = Math.max(W, H) * 0.42;
    const amb = shctx.createRadialGradient(cx, cy, r * 0.1, cx, cy, r);
    amb.addColorStop(0, ROOM_OPEN ? 'rgba(0,0,0,0.34)' : 'rgba(0,0,0,0.10)');
    amb.addColorStop(1, 'rgba(0,0,0,0)');
    shctx.fillStyle = amb;
    shctx.fillRect(0, 0, W, H);

    shctx.globalCompositeOperation = 'source-over';
    vctx.drawImage(shadowLayer, 0, 0);
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
        // An engine is EQUIPMENT: it is connected or it is not, and "ว่าง"
        // (idle, as in waiting for work) says something about a colleague
        // that is simply not true of a renderer.
        const doing = a.bridge
            ? (a.state === 'offline' ? 'ไม่ได้เชื่อมต่อ' : (a.lastTool || 'พร้อมใช้งาน'))
            : a.state === 'offline' ? 'ไม่อยู่'
                : a.state === 'working' ? (a.lastTool || 'ทำงานอยู่')
                    : 'ว่าง';
        html.push(
            `<div class="plate${a.state === 'offline' ? ' is-off' : ''}${a.bridge ? ' is-rig' : ''}" ` +
            `style="--pc:${agentColor(a.id)};left:${(d.desk.x * sx).toFixed(1)}px;top:${((d.desk.y + 14) * sy).toFixed(1)}px">` +
            `<span class="who">${esc(label(a.label || a.id))}</span>` +
            (a.bridge ? `<span class="badge rig">เครื่องมือ</span>` : '') +
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
/** Is the room lit? Dark = everybody has gone home and nothing may be said.
 *  Owner (2026-09-20): "ปิดไฟปิดห้อง ทุกคนออกไปหมด". */
let ROOM_OPEN = true;
/** Is he asleep at his spot? Only true while the light is off. */
let BOSS_ASLEEP = false;
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
    // Read before anything uses it: the agent list and the boss both branch on
    // whether the light is on.
    if (typeof p.roomOpen === 'boolean') ROOM_OPEN = p.roomOpen;
    const hadAgents = AGENTS.length;
    // Nobody is drawn in a dark room. The seats were cleared when it closed;
    // presence only says an MCP process is alive somewhere, which is not the
    // same as being HERE, and drawing them at desks would say the room is
    // still in session.
    AGENTS = ROOM_OPEN ? (p.agents || []).slice().sort((a, b) => a.id.localeCompare(b.id)) : [];
    if (AGENTS.length !== hadAgents) layoutDesks();

    MESSAGES = p.messages || [];
    DECISIONS = p.decisions || [];
    if (typeof p.roomOpen === 'boolean') {
        ROOM_OPEN = p.roomOpen;
        const lb = document.getElementById('room-light');
        if (lb) {
            lb.textContent = ROOM_OPEN ? '💡' : '🌑';
            lb.title = ROOM_OPEN
                ? 'ไฟห้องเปิดอยู่ — กดเพื่อปิดห้องและให้ทุกคนออก'
                : 'ห้องปิดไฟอยู่ — ไม่มีใครอยู่ในห้อง กดเพื่อเปิด';
        }
        document.body.classList.toggle('room-dark', !ROOM_OPEN);
    }
    BROKER = p.broker || null;

    // Perform only what is NEW. The first payload is the backlog, and replaying
    // a day of it as bubbles would say "all of this just happened".
    const fresh = MESSAGES.filter(m => m.id && !SEEN.has(m.id));
    for (const m of MESSAGES) if (m.id) SEEN.add(m.id);
    if (PRIMED) {
        for (const m of fresh.slice(-4)) {
            const c = agentColor(m.from);
            if (m.from === 'owner') {
                BOSS = { until: T + 260, text: firstLine(m.body) };
                // He says it with his body too. `instruct` runs once and the
                // pack sends him back to idle on its own, so nothing here has
                // to remember to put him back.
                try { BOSS_AV?.play('instruct', { loop: false }); } catch { /* pack still loading */ }
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

    // Sitting here and LISTENING are different facts, and the owner needs the
    // second one before they type an order: an agent at a desk that never
    // joined the room will not hear a word of it. That is the whole point of
    // the separate lane — a session working on something else stays quiet.
    // The boss switch: what the broker is doing, and a way to stop it. A room
    // where nobody answered and a room where nothing was ever going to answer
    // look identical without this.
    const bb = document.getElementById('room-broker');
    if (bb) {
        const st = (BROKER && BROKER.state) || 'stopped';
        const svc = (BROKER && BROKER.service) || null;
        const label = { running: 'บอสจัดสรรงาน ✓', adopted: 'บอสจัดสรรงาน (ตัวอื่นคุม)',
                        stopped: 'บอสหยุด — กดเพื่อเริ่ม', failed: 'บอสเริ่มไม่ขึ้น' }[st] || st;
        // The service is the half the owner cannot see: whether anything will
        // still be dispatching after this window closes.
        const svcTag = !svc ? ''
            : !svc.installed ? ' · ไม่มี service'
            : svc.state === 'running' ? ' · service ✓'
            : ' · service ' + svc.state;
        bb.textContent = label + svcTag;
        bb.className = st === 'running' ? 'on' : st === 'adopted' ? 'warn' : st === 'failed' ? 'warn' : 'off';
        bb.title = [
            'คลิก = เริ่ม/หยุดบอสในแอปนี้',
            'คลิกขวา = ติดตั้ง/ถอน Windows Service (ทำงานต่อแม้ปิดแอป)',
            svc && svc.installed ? `service: ${svc.state}` : 'service: ยังไม่ได้ติดตั้ง',
            '', ...((BROKER && BROKER.tail) || []),
        ].join('\n');
    }

    const people = AGENTS.filter(a => !a.bridge);
    const rigs = AGENTS.filter(a => a.bridge);
    const online = people.filter(a => a.state !== 'offline').length;
    const listening = people.filter(a => a.inRoom).length;
    const rigsUp = rigs.filter(a => a.state !== 'offline').length;
    document.getElementById('room-sub').textContent =
        `${online} อยู่ในห้อง · ฟังอยู่ ${listening} · ${people.length} ที่นั่ง`
        + (rigs.length ? ` · เครื่องมือ ${rigsUp}/${rigs.length}` : '');
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

// The boss switch. Asks the client to start or stop the broker; the page has
// no business starting a process that spawns agents, so all it does is ask.
document.getElementById('room-broker')?.addEventListener('click', () => {
    post({ type: 'officeBroker' });
});

// The light switch. Turning it off is the owner sending everybody home; the
// room cannot talk to itself in the dark, which is the whole point of it.
document.getElementById('room-light')?.addEventListener('click', () => {
    const on = !ROOM_OPEN;
    if (!on && !confirm('ปิดไฟปิดห้อง?\n\nทุกคนจะออกจากห้อง ไม่มีใครถูกเรียกและไม่มีใครพูดได้จนกว่าจะเปิดไฟใหม่')) return;
    post({ type: 'officeRoomLight', on });
});

// Right-click installs or removes the Windows Service — the half that keeps
// dispatching after this window is closed. Confirmed here because it is an
// administrative change to the machine, and elevated by the CLIENT, which is
// the only thing that may ask for it.
document.getElementById('room-map')?.addEventListener('click', () => {
    location.href = 'tools/mask-paint.html';
});

document.getElementById('room-broker')?.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    const svc = (BROKER && BROKER.service) || null;
    const install = !svc || !svc.installed;
    const ok = confirm(install
        ? 'ติดตั้ง BrainX Agent Broker เป็น Windows Service?\n\nจะจัดสรรงานต่อแม้ปิดโปรแกรม — ต้องยืนยันสิทธิ์ผู้ดูแล'
        : 'ถอน Windows Service ออก?\n\nปิดโปรแกรมแล้วจะไม่มีใครเรียก agent เข้ามาทำงาน');
    if (ok) post({ type: 'officeBrokerService', action: install ? 'install' : 'uninstall' });
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

// ── looking at the map itself ───────────────────────────────────────

/* `?map=1` paints the room's rules over the room: every walkable cell, the
 * furniture that blocks, the spots and the route he is on. It exists because
 * the polygons were measured off the plate by eye, and the only honest way to
 * check a hand-measured map is to look at it on top of the picture. */
const MAP_DEBUG = new URLSearchParams(location.search).has('map');

function drawMapDebug() {
    const f = PLATE_FIT, g = ROOM_MAP.grid;
    const cw = (f.w / ROOM_MAP.GW) * SCALE, ch = (f.h / ROOM_MAP.GH) * SCALE;
    vctx.save();
    for (let gy = 0; gy < ROOM_MAP.GH; gy++) {
        for (let gx = 0; gx < ROOM_MAP.GW; gx++) {
            if (!g[gy * ROOM_MAP.GW + gx]) continue;
            vctx.fillStyle = 'rgba(80,255,170,0.20)';
            vctx.fillRect((f.x + (gx / ROOM_MAP.GW) * f.w) * SCALE,
                          (f.y + (gy / ROOM_MAP.GH) * f.h) * SCALE, cw - 1, ch - 1);
        }
    }
    const trace = (poly, colour) => {
        vctx.strokeStyle = colour; vctx.lineWidth = 2; vctx.beginPath();
        poly.forEach(([x, y], i) => {
            const X = (f.x + x * f.w) * SCALE, Y = (f.y + y * f.h) * SCALE;
            if (i) vctx.lineTo(X, Y); else vctx.moveTo(X, Y);
        });
        vctx.closePath(); vctx.stroke();
    };
    trace(ROOM_MAP.FLOOR, 'rgba(120,220,255,0.9)');
    for (const b of ROOM_MAP.BLOCKS) trace(b.poly, 'rgba(255,90,120,0.9)');
    for (const o of ROOM_MAP.OCCLUDERS) trace(o.poly, 'rgba(255,200,80,0.55)');
    for (const s of ROOM_MAP.SPOTS) {
        vctx.fillStyle = '#ffd166';
        vctx.fillRect((f.x + s.x * f.w) * SCALE - 3, (f.y + s.y * f.h) * SCALE - 3, 6, 6);
        vctx.fillStyle = '#fff'; vctx.font = '12px monospace';
        vctx.fillText(s.key, (f.x + s.x * f.w) * SCALE + 6, (f.y + s.y * f.h) * SCALE);
    }
    if (BOSS_PATH) {
        vctx.strokeStyle = '#8ef'; vctx.lineWidth = 2; vctx.beginPath();
        BOSS_PATH.forEach((p, i) => {
            const X = (f.x + p.x * f.w) * SCALE, Y = (f.y + p.y * f.h) * SCALE;
            if (i) vctx.lineTo(X, Y); else vctx.moveTo(X, Y);
        });
        vctx.stroke();
    }
    vctx.restore();
}

// ── the boss's animation pack ───────────────────────────────────────
//
// Imported dynamically, and allowed to fail. The three atlases are ~6MB and
// the module is ESM while this file is a classic script; a missing or broken
// pack should cost the boss his animation, not take the whole office down
// with it — drawBoss() is still here for exactly that case.
(async () => {
    try {
        const { BrainXAvatar } = await import('./avatar/brainx-avatar.js');
        BOSS_AV = await BrainXAvatar.load('./avatar/', { scale: 1 });
        plateFit();
        // A painted mask and painted seats win over the hand-measured
        // polygons when they exist; both resolve either way, so a vault
        // without them behaves exactly as before.
        try {
            const [hasMask, hasSeats] = await Promise.all([
                ROOM_MAP.loadRoomMask(), ROOM_MAP.loadRoomSeats(),
            ]);
            if (hasMask) { OCC_PIECES = null; console.info('cowork: using painted room mask'); }
            if (hasSeats) console.info('cowork: using painted seats');
        } catch { /* fall back to the polygons */ }
        // Build the walk grid before the first click rather than on it: 56×42
        // point-in-polygon tests are cheap, but not on the frame somebody is
        // waiting to see him start moving.
        ROOM_MAP.buildGrid();
        bossPlace(BOSS_HOME);
        bossRelayout();
        BOSS_AV.play('idle');
        // He starts settled rather than mid-stride, and wanders from there.
        BOSS_PLAN = { spot: BOSS_HOME, phase: 'resting', until: performance.now() + 2500 };
        // Clicking the room is how the owner plays with him: on him he reacts,
        // anywhere else he walks there. Registered only once the pack is up,
        // so a click before that does nothing rather than throwing.
        cv.addEventListener('click', onRoomClick);
        cv.style.cursor = 'pointer';
    } catch (e) {
        console.warn('cowork: boss avatar pack unavailable —', e?.message || e);
    }
})();

resize();
requestAnimationFrame(frame);
post({ type: 'officeReady' });
if (new URLSearchParams(location.search).has('demo')) demo();
