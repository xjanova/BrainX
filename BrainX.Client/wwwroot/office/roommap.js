/* The room, as a place with rules.
 *
 * Owner (2026-09-20): "มันต้องกำหนดจุดในภาพว่าตรงไหนนั่งได้ ตรงไหนกินน้ำได้
 * ตรงไหนเดินเข้าไปได้ไม่ได้ เหมือนเกมนั่นแหล่ะ" and "ตรงไหนต้องบังตัวละคร
 * บริเวณมุมบนซ้าย ก็ต้องคำนึง".
 *
 * The plate (art/room.webp) is a picture, and a picture has no idea that a
 * sofa is solid. Everything that makes it behave like a room lives here, in
 * three layers, all measured off the plate in NORMALISED coordinates (0..1 of
 * the plate's width and height) so they survive any window size:
 *
 *   FLOOR      the walkable shape — the wooden floor, the rug in the middle
 *              and the patterned rug the sofa stands on, as polygons.
 *   BLOCKS     furniture you cannot walk through. Subtracted from FLOOR.
 *   OCCLUDERS  furniture that is IN FRONT of a character standing behind it.
 *              Each one is a cut-out of the plate, re-drawn over the sprite
 *              when the character's feet are further from the viewer than the
 *              piece's own base line. No new art: the cut is taken from the
 *              same plate the room is already drawing.
 *   SPOTS      places worth going to, and what you do when you get there.
 *
 * Coordinates were read off a 20×20 measuring grid laid over the plate, not
 * guessed. In isometric art the floor is a diamond, so the polygons are
 * diamonds too — an axis-aligned rectangle would either cut off the corners
 * of the room or let people walk into the walls.
 */

// ── the shape you can stand on ──────────────────────────────────────

/** The wooden floor plus both rugs, as one diamond ring around the room.
 *  Traced clockwise from the coffee-bar corner. */
const FLOOR = [
    [0.255, 0.600],   // by the coffee bar, where wood meets the lower rug
    [0.278, 0.345],   // up the left wall, past the cabinets
    [0.400, 0.235],   // the corner under the pictures
    [0.520, 0.215],   // in front of the server racks
    [0.628, 0.268],   // toward the glass room
    [0.660, 0.330],   // the glass room's near corner
    [0.800, 0.455],   // right wall, above the bookshelf
    [0.760, 0.560],   // in front of the bookshelf
    [0.660, 0.630],   // the right planter
    [0.585, 0.760],   // the balcony rail, bottom right
    [0.470, 0.870],   // the bottom corner of the patterned rug
    [0.330, 0.810],   // along its lower edge
    [0.252, 0.660],   // back up to the coffee bar
];

/** Things in the room you cannot walk through. Each is a polygon on the same
 *  normalised plate, and each is a piece of furniture you can SEE — a blocker
 *  that does not match something painted is a wall nobody understands. */
const BLOCKS = [
    // The bar runs down the left wall on the isometric diagonal, so its front
    // edge drops as it goes right — an axis-aligned box here either ate the
    // floor somebody walks on or let them stand inside the espresso machine.
    { key: 'counter',  poly: [[0.000, 0.360], [0.285, 0.480], [0.285, 0.625], [0.000, 0.700]] },
    // The sofa's right arm stops short of the planter beside it. Those two
    // boxes used to overlap, which walled off the whole patterned rug in
    // front of the sofa — the seats were walkable, and completely unreachable,
    // which is the kind of thing only a connectivity check finds.
    { key: 'sofa',     poly: [[0.300, 0.665], [0.360, 0.628], [0.505, 0.640], [0.556, 0.700], [0.505, 0.762], [0.365, 0.762]] },
    // Bookshelf, its lamp and the low cabinet beside it. Measured from the
    // front edge of the shelf, not the wall behind it: the wooden floor in
    // front of the books is where somebody stands to read them.
    { key: 'shelf',    poly: [[0.700, 0.555], [0.900, 0.500], [0.945, 0.680], [0.735, 0.775]] },
    { key: 'sidetbl',  poly: [[0.715, 0.400], [0.830, 0.370], [0.860, 0.470], [0.740, 0.500]] },
    { key: 'glass',    poly: [[0.610, 0.080], [0.820, 0.180], [0.805, 0.330], [0.645, 0.330]] },
    { key: 'racks',    poly: [[0.400, 0.050], [0.615, 0.140], [0.615, 0.235], [0.405, 0.215]] },
    { key: 'cabinets', poly: [[0.060, 0.110], [0.330, 0.010], [0.390, 0.215], [0.268, 0.325]] },
    { key: 'plant-l',  poly: [[0.145, 0.300], [0.235, 0.300], [0.235, 0.440], [0.145, 0.440]] },
    // Both planters are drawn tight to the pot rather than to the leaves: a
    // person walks past foliage, and a generous box here is what closed the
    // two gaps the sofa rug is reached through.
    { key: 'plant-c',  poly: [[0.232, 0.615], [0.300, 0.615], [0.300, 0.695], [0.232, 0.695]] },
    { key: 'plant-r',  poly: [[0.585, 0.635], [0.662, 0.625], [0.666, 0.770], [0.585, 0.778]] },
    { key: 'rail',     poly: [[0.575, 0.745], [0.745, 0.660], [0.800, 0.790], [0.615, 0.880]] },
];

/**
 * Pieces of the plate that must be re-drawn OVER a character.
 *
 * `base` is the piece's own depth line in normalised Y: a character whose feet
 * are ABOVE it (smaller y — further into the room) is behind the piece and
 * gets covered; one standing in front of it is not. That single number is what
 * an isometric room needs instead of a full depth buffer, because every one of
 * these objects sits flat on the floor.
 *
 * The polygons are deliberately a little generous at the top, since what is
 * being clipped is the picture itself — an extra few pixels of wall re-drawn
 * over the wall changes nothing, while a few pixels short leaves a sliver of
 * character floating through the sofa.
 */
const OCCLUDERS = [
    // The owner's example: the wall, cabinets and hanging plants in the top
    // left are nearer the viewer than the floor behind them.
    { key: 'topleft',  base: 0.350, poly: [[0.000, 0.000], [0.400, 0.000], [0.400, 0.250], [0.270, 0.360], [0.000, 0.300]] },
    { key: 'plant-l',  base: 0.450, poly: [[0.120, 0.270], [0.250, 0.270], [0.250, 0.470], [0.120, 0.470]] },
    { key: 'counter',  base: 0.600, poly: [[0.000, 0.330], [0.285, 0.440], [0.285, 0.640], [0.000, 0.660]] },
    { key: 'sofa',     base: 0.775, poly: [[0.275, 0.610], [0.580, 0.620], [0.580, 0.800], [0.275, 0.800]] },
    { key: 'shelf',    base: 0.650, poly: [[0.640, 0.410], [0.930, 0.390], [0.960, 0.680], [0.660, 0.680]] },
    { key: 'plant-r',  base: 0.760, poly: [[0.545, 0.580], [0.680, 0.580], [0.680, 0.790], [0.545, 0.790]] },
    { key: 'rail',     base: 0.880, poly: [[0.480, 0.700], [0.820, 0.620], [0.860, 0.900], [0.500, 0.940]] },
];

/**
 * Where a person goes, and what they do there.
 *
 * `x,y` is where the FEET land — for a seat that is the floor in front of the
 * cushion, not the cushion itself, because the pack anchors every frame at the
 * sole. `face` steers the idle direction so nobody sits with their back to the
 * room. `stay` is a range in seconds, so two visits never feel like a loop.
 */
const SPOTS = [
    { key: 'rug',      x: 0.495, y: 0.560, act: 'think', face: 'se', stay: [6, 14], home: true },
    // A seat is TWO places. `x,y` is where he walks to — floor, in front of
    // the cushion, somewhere a path can actually reach — and `seat` is where
    // the sitting frame is drawn, on the cushion itself. Without the split,
    // the only reachable point is the floor and he sits on thin air; with the
    // seat as the destination, no path exists at all because the sofa is
    // solid, which is exactly what the first version of this map did.
    { key: 'sofa-l',   x: 0.340, y: 0.745, act: 'sit', face: 'ne', stay: [16, 38], seat: { x: 0.368, y: 0.712 } },
    { key: 'sofa-r',   x: 0.520, y: 0.795, act: 'sit', face: 'nw', stay: [16, 38], seat: { x: 0.500, y: 0.752 } },
    { key: 'counter',  x: 0.310, y: 0.570, act: 'drink', face: 'nw', stay: [7, 14] },
    { key: 'shelf',    x: 0.660, y: 0.560, act: 'read',  face: 'ne', stay: [9, 20] },
    { key: 'glass',    x: 0.595, y: 0.375, act: 'phone', face: 'ne', stay: [8, 18] },
    { key: 'racks',    x: 0.505, y: 0.285, act: 'point', face: 'ne', stay: [5, 11] },
    { key: 'window',   x: 0.690, y: 0.455, act: 'think', face: 'ne', stay: [6, 14] },
];

// ── geometry ────────────────────────────────────────────────────────

function pointInPoly(px, py, poly) {
    let inside = false;
    for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
        const [xi, yi] = poly[i], [xj, yj] = poly[j];
        if ((yi > py) !== (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
    }
    return inside;
}

/** Can somebody stand here? Normalised plate coordinates. */
function isWalkable(nx, ny) {
    if (!pointInPoly(nx, ny, FLOOR)) return false;
    for (const b of BLOCKS) if (pointInPoly(nx, ny, b.poly)) return false;
    return true;
}

// ── the grid the walking is planned on ──────────────────────────────

/* Coarse on purpose. 56×42 over the plate is about one cell per 26×26 plate
 * pixels — finer than a person is wide, which is all a path needs, and small
 * enough that a full A* runs in well under a millisecond on a click. */
const GW = 56, GH = 42;
let GRID = null;

function gridIndex(gx, gy) { return gy * GW + gx; }
function cellCentre(gx, gy) { return { x: (gx + 0.5) / GW, y: (gy + 0.5) / GH }; }

function buildGrid() {
    GRID = new Uint8Array(GW * GH);
    for (let gy = 0; gy < GH; gy++) {
        for (let gx = 0; gx < GW; gx++) {
            const c = cellCentre(gx, gy);
            GRID[gridIndex(gx, gy)] = isWalkable(c.x, c.y) ? 1 : 0;
        }
    }
    return GRID;
}

function gridAt(nx, ny) {
    return {
        gx: Math.min(GW - 1, Math.max(0, Math.floor(nx * GW))),
        gy: Math.min(GH - 1, Math.max(0, Math.floor(ny * GH))),
    };
}

/** The nearest cell you could actually stand in — used when a click lands on
 *  the sofa or outside the room, so a misclick walks to the edge of the thing
 *  rather than doing nothing. */
function nearestWalkable(nx, ny) {
    if (!GRID) buildGrid();
    const start = gridAt(nx, ny);
    if (GRID[gridIndex(start.gx, start.gy)]) return { x: nx, y: ny };
    for (let r = 1; r < Math.max(GW, GH); r++) {
        for (let dy = -r; dy <= r; dy++) {
            for (let dx = -r; dx <= r; dx++) {
                if (Math.max(Math.abs(dx), Math.abs(dy)) !== r) continue;
                const gx = start.gx + dx, gy = start.gy + dy;
                if (gx < 0 || gy < 0 || gx >= GW || gy >= GH) continue;
                if (GRID[gridIndex(gx, gy)]) return cellCentre(gx, gy);
            }
        }
    }
    return null;
}

/**
 * A* from one normalised point to another, returned as normalised waypoints.
 *
 * Eight-way, with diagonals refused when either orthogonal neighbour is solid
 * — otherwise a path happily cuts the corner of the counter and the character
 * walks through the espresso machine.
 */
function findPath(from, to) {
    if (!GRID) buildGrid();
    const startCell = nearestWalkable(from.x, from.y);
    const goalCell = nearestWalkable(to.x, to.y);
    if (!startCell || !goalCell) return [];

    const s = gridAt(startCell.x, startCell.y), g = gridAt(goalCell.x, goalCell.y);
    const sIdx = gridIndex(s.gx, s.gy), gIdx = gridIndex(g.gx, g.gy);
    if (sIdx === gIdx) return [{ x: to.x, y: to.y }];

    const open = [sIdx];
    const cameFrom = new Map();
    const gScore = new Map([[sIdx, 0]]);
    const h = (i) => {
        const x = i % GW, y = (i / GW) | 0;
        return Math.hypot(x - g.gx, y - g.gy);
    };
    const fScore = new Map([[sIdx, h(sIdx)]]);
    const closed = new Set();

    let guard = GW * GH * 4;
    while (open.length && guard-- > 0) {
        open.sort((a, b) => (fScore.get(a) ?? 1e9) - (fScore.get(b) ?? 1e9));
        const cur = open.shift();
        if (cur === gIdx) break;
        closed.add(cur);
        const cx = cur % GW, cy = (cur / GW) | 0;

        for (let dy = -1; dy <= 1; dy++) {
            for (let dx = -1; dx <= 1; dx++) {
                if (!dx && !dy) continue;
                const nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= GW || ny >= GH) continue;
                const nIdx = gridIndex(nx, ny);
                if (!GRID[nIdx] || closed.has(nIdx)) continue;
                if (dx && dy && (!GRID[gridIndex(cx + dx, cy)] || !GRID[gridIndex(cx, cy + dy)])) continue;

                const step = (dx && dy) ? 1.414 : 1;
                const tentative = (gScore.get(cur) ?? 1e9) + step;
                if (tentative < (gScore.get(nIdx) ?? 1e9)) {
                    cameFrom.set(nIdx, cur);
                    gScore.set(nIdx, tentative);
                    fScore.set(nIdx, tentative + h(nIdx));
                    if (!open.includes(nIdx)) open.push(nIdx);
                }
            }
        }
    }

    if (!cameFrom.has(gIdx) && sIdx !== gIdx) return [];
    const cells = [];
    for (let i = gIdx; i !== undefined && i !== sIdx; i = cameFrom.get(i)) {
        cells.push(i);
        if (cells.length > GW * GH) break;
    }
    cells.reverse();

    // Straighten it. A* on a grid produces a staircase; dropping every
    // waypoint that the previous one can already see turns it back into the
    // two or three straight legs a person would actually walk.
    const pts = cells.map(i => cellCentre(i % GW, (i / GW) | 0));
    pts[pts.length - 1] = { x: to.x, y: to.y };
    const out = [];
    let anchor = { x: from.x, y: from.y };
    for (let i = 0; i < pts.length; i++) {
        const next = pts[i + 1];
        if (next && clearLine(anchor, next)) continue;
        out.push(pts[i]);
        anchor = pts[i];
    }
    return out;
}

/** Is the straight line between two normalised points entirely walkable?
 *  Sampled at roughly one grid cell, which is the resolution the path was
 *  planned at — checking finer would reject lines the walker can follow. */
function clearLine(a, b) {
    const steps = Math.ceil(Math.hypot((b.x - a.x) * GW, (b.y - a.y) * GH));
    for (let i = 1; i <= steps; i++) {
        const t = i / steps;
        if (!isWalkable(a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t)) return false;
    }
    return true;
}

/** The spot nearest a normalised point, if the click was close enough to mean
 *  it. `within` is in plate units — about a seventh of the room's width. */
function spotNear(nx, ny, within = 0.075) {
    let best = null, bestD = Infinity;
    for (const s of SPOTS) {
        const d = Math.hypot(s.x - nx, s.y - ny);
        if (d < bestD) { bestD = d; best = s; }
    }
    return bestD <= within ? best : null;
}

const ROOM_MAP = { FLOOR, BLOCKS, OCCLUDERS, SPOTS, GW, GH,
                   isWalkable, nearestWalkable, findPath, spotNear, buildGrid,
                   pointInPoly, get grid() { return GRID || buildGrid(); } };
