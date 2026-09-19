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
const SCALE = 3;
const TILE_W = 32, TILE_H = 16;     // isometric tile, 2:1 like every iso game

const cv = document.getElementById('floor');
const ctx = cv.getContext('2d', { alpha: false });
const overlay = document.getElementById('overlay');

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
    cv.width = CW;
    cv.height = CH;
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
let PRIMED = false;       // first payload is backlog: show it, don't perform it
const BUBBLES = [];       // {agent,text,color,until}
const PACKETS = [];       // {from,to,color,t0,ms}
let BOSS = null;          // {until,text} — the owner, standing in the room
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

function layoutRoom() {
    // The diamond is twice as wide as it is tall, so width and height give two
    // different budgets for the same number of cells; the smaller wins.
    const byW = Math.floor((CW - 26) / (TILE_W / 2));
    const byH = Math.floor((CH - 52) / (TILE_H / 2));
    const S = Math.max(4, Math.min(byW, byH));
    ROOM.C = Math.max(2, Math.round(S / 2));
    ROOM.R = Math.max(2, S - ROOM.C);

    // Place cell (0,0) so the finished diamond sits in the middle of the room,
    // with the extra headroom going to the top where the walls are drawn.
    ORIGIN = {
        x: Math.round(CW / 2 - (ROOM.C - ROOM.R) * (TILE_W / 4)),
        y: Math.round((CH - ((ROOM.C + ROOM.R - 2) * (TILE_H / 2) + TILE_H)) / 2) + 8,
    };
}

/** How many desks fit across this room. */
function deskCols(n) {
    const fits = Math.max(1, Math.floor((ROOM.C - 1) / SPACING) + 1);
    return Math.max(1, Math.min(fits, Math.min(4, Math.ceil(Math.sqrt(n * 1.4)))));
}

function layoutDesks() {
    layoutRoom();
    DESKS.clear();
    const n = AGENTS.length || 1;
    const cols = deskCols(n);
    const rows = Math.ceil(n / cols);

    // Desks sit one cell in from the back walls, and the block is centred in
    // whatever floor is left — so an office with two people in it is not two
    // desks huddled in a corner of a large room.
    const usedC = (cols - 1) * SPACING, usedR = (rows - 1) * SPACING;
    const padC = Math.max(1, Math.floor((ROOM.C - 1 - usedC) / 2));
    const padR = Math.max(1, Math.floor((ROOM.R - 1 - usedR) / 2));

    AGENTS.forEach((a, i) => {
        const gx = padC + (i % cols) * SPACING;
        const gy = padR + Math.floor(i / cols) * SPACING;
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

// ── the room ────────────────────────────────────────────────────────

function drawRoom() {
    px(0, 0, CW, CH, '#070812');

    drawWalls();
    for (let gy = 0; gy < ROOM.R; gy++)
        for (let gx = 0; gx < ROOM.C; gx++)
            tile(gx, gy, (gx + gy) % 2 ? '#141838' : '#10142e');

    drawProps();

    // A soft pool of light under each occupied desk. Drawn before the desks so
    // it reads as light on the floor rather than a halo around the furniture.
    for (const a of AGENTS) {
        const d = DESKS.get(a.id);
        if (!d || a.state === 'offline') continue;
        const g = ctx.createRadialGradient(d.desk.x, d.desk.y + 8, 2, d.desk.x, d.desk.y + 8, 26);
        g.addColorStop(0, 'rgba(120,200,255,0.13)');
        g.addColorStop(1, 'rgba(120,200,255,0)');
        ctx.fillStyle = g;
        ctx.fillRect(d.desk.x - 28, d.desk.y - 14, 56, 40);
    }

    // Back to front, so a desk nearer the viewer covers the one behind it.
    const order = [...AGENTS].sort((a, b) => {
        const A = DESKS.get(a.id), B = DESKS.get(b.id);
        return (A.gx + A.gy) - (B.gx + B.gy);
    });
    for (const a of order) drawWorkstation(a);

    if (BOSS && T < BOSS.until) drawBoss();
    drawPackets();
    scanlines();
}

/** The two back walls, with the things an office has on them.
 *
 *  Drawn before the floor so the floor's front edge overlaps their base — the
 *  join is what makes the room look built rather than assembled. */
function drawWalls() {
    const H = 30;
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
    // The walls have to be clearly LIGHTER than the floor or the room has no
    // back — the first pass used #161a3a against a #141838 floor and the two
    // were indistinguishable, so the office read as furniture on a plain.
    quad(a, b, '#242a52');             // left wall, in shadow
    quad(b, c, '#2e3566');             // right wall, catching the light

    // Where the walls meet, so the corner is a corner and not a seam.
    px(b.x - 1, b.y - H, 2, H, '#39417a');

    // A window on the right wall. Night outside, because this room is mostly
    // watched in the evening and a bright window would blow out the scene.
    const wx = b.x + (c.x - b.x) * 0.55, wy = b.y + (c.y - b.y) * 0.55;
    px(wx - 9, wy - 25, 18, 12, '#0a1226');
    px(wx - 8, wy - 24, 16, 10, '#0d1b3a');
    for (let i = 0; i < 5; i++)
        px(wx - 7 + ((i * 7) % 15), wy - 22 + ((i * 5) % 8), 1, 1, '#7fd4ff');

    // A whiteboard on the left wall, with something already on it.
    const bx = a.x + (b.x - a.x) * 0.5, by = a.y + (b.y - a.y) * 0.5;
    px(bx - 8, by - 26, 17, 12, '#2a2f55');
    px(bx - 7, by - 25, 15, 10, '#cdd6f5');
    px(bx - 5, by - 22, 9, 1, '#6a7299');
    px(bx - 5, by - 19, 6, 1, '#6a7299');
    px(bx - 5, by - 16, 11, 1, '#e08a5a');
}

/** The things that make it an office rather than a room with desks in it.
 *  Placed relative to the room, so they move out of the way instead of being
 *  sat on when a fifth agent connects and the desk block grows. */
function drawProps() {
    // A plant in the back corner.
    const pl = iso(ROOM.C - 1, 0);
    isoSolid(pl.x, pl.y + 6, 4, 2, 5, '#4a3a2a', '#2a2018', '#37291e');
    px(pl.x - 4, pl.y - 2, 3, 5, '#3e7a4a');
    px(pl.x + 1, pl.y - 4, 3, 7, '#4b9159');
    px(pl.x - 1, pl.y - 6, 3, 8, '#43824f');

    // A rug at the front, where the owner stands. The room's empty half was
    // reading as unfinished rather than as floor.
    const r0 = bossSpot();
    ctx.globalAlpha = 0.5;
    ctx.fillStyle = '#2a2450';
    ctx.beginPath();
    ctx.moveTo(r0.x, r0.y + 4 - TILE_H);
    ctx.lineTo(r0.x + TILE_W, r0.y + 4);
    ctx.lineTo(r0.x, r0.y + 4 + TILE_H);
    ctx.lineTo(r0.x - TILE_W, r0.y + 4);
    ctx.closePath();
    ctx.fill();
    ctx.globalAlpha = 1;
}

/** Desk, monitor, chair, and whoever is sitting in it. */
function drawWorkstation(a) {
    const d = DESKS.get(a.id);
    if (!d) return;
    const { x, y } = d.desk;
    const c = agentColor(a.id);
    const off = a.state === 'offline';

    // Desk, as an isometric box rather than a flat bar — the first version
    // drew it as a rectangle and it read as a shelf hovering over the tiles,
    // because it was the only thing in the room not in the room's projection.
    isoSolid(x, y - 4, 15, 7, 5,
        off ? '#262a49' : '#343a63',
        off ? '#171a2e' : '#1f2340',
        off ? '#1d2138' : '#282d4d');

    const lit = a.state === 'working';

    // Monitor: a thin slab standing on the desk, screen facing the viewer.
    px(x - 2, y - 11, 4, 3, '#1b1f38');                       // stand
    px(x - 8, y - 22, 16, 12, '#10132a');                     // bezel
    px(x - 7, y - 21, 14, 10, off ? '#0b0d1a' : (lit ? c : '#1d2244'));
    if (!off && lit) {
        // Content, faked as rows that scroll — the flicker is what makes a lit
        // screen read as "being used" rather than "switched on".
        ctx.globalAlpha = 0.38;
        for (let i = 0; i < 4; i++) {
            const w = 3 + ((T / 6 + i * 3) | 0) % 10;
            px(x - 6, y - 20 + i * 2, w, 1, '#06121c');
        }
        ctx.globalAlpha = 1;
    }
    // Keyboard, so the typing animation has something to land on.
    px(x - 6, y - 4, 12, 2, off ? '#22263f' : '#2b3050');

    if (off) { isoSolid(x, y + 7, 6, 3, 4, '#1e2239', '#14172a', '#191d33'); return; }
    drawPerson(x, y - 2, c, a);
}

/** A person, about sixteen logical pixels tall. */
function drawPerson(x, y, c, a) {
    const working = a.state === 'working';
    // Typing is a two-frame bob. Slow enough to read at a glance, fast enough
    // that "typing" and "sitting still" are never confused.
    const bob = working ? ((T >> 2) % 2) : 0;
    const top = y + 1 - bob;

    px(x - 4, top + 6, 8, 6, shade(c, -0.35));   // torso
    px(x - 3, top, 6, 6, '#f0cfae');             // head
    px(x - 3, top, 6, 2, shade(c, -0.55));       // hair

    // Eyes, and a blink every few seconds so an idle agent still reads as
    // alive rather than as a switched-off portrait.
    const blink = (T % 220) < 6;
    if (!blink) {
        px(x - 2, top + 3, 1, 1, '#2a2438');
        px(x + 1, top + 3, 1, 1, '#2a2438');
    } else {
        px(x - 2, top + 3, 4, 1, '#2a2438');
    }

    // Arms: on the desk when typing, in the lap when idle.
    const armY = working ? top + 7 + (bob ? 0 : 1) : top + 9;
    px(x - 6, armY, 2, 2, '#f0cfae');
    px(x + 4, armY, 2, 2, '#f0cfae');

    px(x - 5, y + 10, 10, 3, shade(c, -0.6));    // chair back
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

/** A faint CRT banding over the whole room. Cheap, and it ties the procedural
 *  sprites together into one picture instead of a set of drawings. */
function scanlines() {
    ctx.globalAlpha = 0.06;
    ctx.fillStyle = '#000';
    for (let y = 0; y < CH; y += 2) ctx.fillRect(0, y, CW, 1);
    ctx.globalAlpha = 1;
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
    const sx = r.width / CW, sy = r.height / CH;
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
            if (DESKS.has(m.from) && DESKS.has(m.to))
                PACKETS.push({ from: m.from, to: m.to, color: c, t0: performance.now(), ms: 900 });
        }
    }
    PRIMED = true;

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
    const agents = [
        { id: 'claude', label: 'Claude', state: 'working', lastTool: 'brain_search', pending: 0 },
        { id: 'codex', label: 'Codex', state: 'working', lastTool: 'agent_inbox', pending: 2, spawned: true },
        { id: 'cluadex', label: 'CluadeX', state: 'idle', lastTool: '', pending: 0 },
        { id: 'gemini', label: 'Gemini', state: 'offline', lastTool: '', pending: 1 },
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
    drawRoom();
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
