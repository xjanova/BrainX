// BrainX Neural Brain — anatomy + layout.
//
// The brain theme's counterpart to layout.js. Same payload in, same shape out
// ({ nodes, edges, galaxies }), so everything scene.js does with a universe —
// physics, pulses, focus, islands, walks, semantic springs — runs unchanged.
// What changes is WHERE a note lives and what a link looks like:
//
//   • every category is a REGION of a human brain, chosen for what that part
//     of a real brain does (logic → frontal lobe, vision → occipital lobe,
//     memory → hippocampus, threat → amygdala …), sized by its note count;
//   • a note is a neuron in the cortical layer of its region;
//   • a wiki-link is a fibre routed the way white matter actually runs —
//     diving under the cortex, crossing between hemispheres through the
//     corpus callosum, reaching the cerebellum through the brainstem —
//     never a straight chord through space.
//
// Pure math, no three.js, so it can be exercised from node. brainvisuals.js
// turns these numbers into meshes; the node test imports this file directly.
//
// Axes: +y superior, +z anterior (the face), +x the subject's LEFT. A person
// facing +z with +y up has their right hand toward -x, so the left
// hemisphere sits at +x and the right one at -x.

import { hashStr, rng, gauss, paletteFor, expertiseByCategory, linkEdges } from './layout.js';

// ── small math ──────────────────────────────────────────────────────────

const clamp = (v, lo, hi) => (v < lo ? lo : v > hi ? hi : v);
function smoothstep(e0, e1, x) {
    const t = clamp((x - e0) / (e1 - e0), 0, 1);
    return t * t * (3 - 2 * t);
}
/** Polynomial smooth minimum: min(a, b) with the corner rounded over k. */
function smin(a, b, k) {
    const h = clamp(0.5 + 0.5 * (b - a) / k, 0, 1);
    return b + (a - b) * h - k * h * (1 - h);
}
const groove = (dist, w) => Math.exp(-(dist / w) * (dist / w));

function norm3(v) {
    const l = Math.hypot(v.x, v.y, v.z) || 1;
    v.x /= l; v.y /= l; v.z /= l;
    return v;
}
function cross(a, b) {
    return { x: a.y * b.z - a.z * b.y, y: a.z * b.x - a.x * b.z, z: a.x * b.y - a.y * b.x };
}

/** Distance from (z, y) to a polyline given as [[z, y], ...]. */
function distPolyline(z, y, pts) {
    let best = Infinity;
    for (let i = 0; i < pts.length - 1; i++) {
        const d = distSeg(z, y, pts[i][0], pts[i][1], pts[i + 1][0], pts[i + 1][1]);
        if (d < best) best = d;
    }
    return best;
}
function distSeg(px, py, ax, ay, bx, by) {
    const vx = bx - ax, vy = by - ay;
    const t = clamp(((px - ax) * vx + (py - ay) * vy) / (vx * vx + vy * vy || 1), 0, 1);
    return Math.hypot(px - (ax + vx * t), py - (ay + vy * t));
}

// ── 3D simplex noise (Gustavson), seeded so every boot folds the same ───
//
// The cortex's gyri come from this. Seeded rather than Math.random because a
// brain whose folds rearranged on every launch would not be the SAME brain,
// and the eye notices that faster than anyone would expect.

function makeNoise3(seed) {
    const rand = rng(seed);
    const p = new Uint8Array(256);
    for (let i = 0; i < 256; i++) p[i] = i;
    for (let i = 255; i > 0; i--) {
        const j = Math.floor(rand() * (i + 1));
        const t = p[i]; p[i] = p[j]; p[j] = t;
    }
    const perm = new Uint8Array(512), pm12 = new Uint8Array(512);
    for (let i = 0; i < 512; i++) { perm[i] = p[i & 255]; pm12[i] = perm[i] % 12; }
    const g = [1,1,0, -1,1,0, 1,-1,0, -1,-1,0, 1,0,1, -1,0,1, 1,0,-1, -1,0,-1, 0,1,1, 0,-1,1, 0,1,-1, 0,-1,-1];
    const F3 = 1 / 3, G3 = 1 / 6;
    const corner = (gi, x, y, z) => {
        let t = 0.6 - x * x - y * y - z * z;
        if (t < 0) return 0;
        t *= t;
        return t * t * (g[gi] * x + g[gi + 1] * y + g[gi + 2] * z);
    };
    return function noise3(xin, yin, zin) {
        const s = (xin + yin + zin) * F3;
        const i = Math.floor(xin + s), j = Math.floor(yin + s), k = Math.floor(zin + s);
        const t = (i + j + k) * G3;
        const x0 = xin - (i - t), y0 = yin - (j - t), z0 = zin - (k - t);
        let i1, j1, k1, i2, j2, k2;
        if (x0 >= y0) {
            if (y0 >= z0)      { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
            else if (x0 >= z0) { i1 = 1; j1 = 0; k1 = 0; i2 = 1; j2 = 0; k2 = 1; }
            else               { i1 = 0; j1 = 0; k1 = 1; i2 = 1; j2 = 0; k2 = 1; }
        } else {
            if (y0 < z0)       { i1 = 0; j1 = 0; k1 = 1; i2 = 0; j2 = 1; k2 = 1; }
            else if (x0 < z0)  { i1 = 0; j1 = 1; k1 = 0; i2 = 0; j2 = 1; k2 = 1; }
            else               { i1 = 0; j1 = 1; k1 = 0; i2 = 1; j2 = 1; k2 = 0; }
        }
        const ii = i & 255, jj = j & 255, kk = k & 255;
        const n0 = corner(pm12[ii + perm[jj + perm[kk]]] * 3, x0, y0, z0);
        const n1 = corner(pm12[ii + i1 + perm[jj + j1 + perm[kk + k1]]] * 3,
                          x0 - i1 + G3, y0 - j1 + G3, z0 - k1 + G3);
        const n2 = corner(pm12[ii + i2 + perm[jj + j2 + perm[kk + k2]]] * 3,
                          x0 - i2 + 2 * G3, y0 - j2 + 2 * G3, z0 - k2 + 2 * G3);
        const n3 = corner(pm12[ii + 1 + perm[jj + 1 + perm[kk + 1]]] * 3,
                          x0 - 1 + 3 * G3, y0 - 1 + 3 * G3, z0 - 1 + 3 * G3);
        return 32 * (n0 + n1 + n2 + n3);
    };
}
const noise3 = makeNoise3(0xB4A1D);

// ── the cerebrum ─────────────────────────────────────────────────────────
//
// Each hemisphere is a radial function around its own centre, written in a
// LATERAL-POSITIVE local frame (x > 0 points away from the midline) so one
// function serves both sides. It starts as an asymmetric ellipsoid and is
// then carved into a brain:
//   • tapered at both poles (a hemisphere is widest over the parietal lobe);
//   • given a floor that is high under the frontal lobe (the orbital
//     surface) and low under the temporal lobe, which is what makes the
//     temporal lobe hang;
//   • cut flat on the medial side, 1.6 units off the midline, which leaves
//     the longitudinal fissure as a real gap between the two halves;
//   • grooved along the principal sulci — lateral (Sylvian), central, pre-
//     and post-central, intraparietal, frontal and superior temporal — the
//     landmarks that make a brain read as a brain rather than a walnut.
// Fine folding (gyri) is added only for the visible shell, on top of this.

export const HEMI_CX = 50, HEMI_CY = 18, HEMI_CZ = -4;
const AX = 52, AYT = 70, AYB = 64, AZF = 114, AZB = 122;
const MEDIAL = 48.4;

export const hemiCenter = (sign) => ({ x: sign * HEMI_CX, y: HEMI_CY, z: HEMI_CZ });

function ellR(dx, dy, dz, ax) {
    const ay = dy > 0 ? AYT : AYB, az = dz > 0 ? AZF : AZB;
    return 1 / Math.sqrt((dx / ax) * (dx / ax) + (dy / ay) * (dy / ay) + (dz / az) * (dz / az));
}

/** The inferior surface: low under the temporal lobe, high under the frontal
 *  lobe (orbital cortex) and rising again toward the occipital pole. */
function floorY(z) {
    const temporal = -56, orbital = -24, occipital = -34;
    return temporal
        + (orbital - temporal) * smoothstep(26, 50, z)
        + (occipital - temporal) * smoothstep(-48, -96, z);
}

/** Smooth hemisphere radius along a unit direction (local, lateral-positive). */
export function hemiBaseRadius(dx, dy, dz) {
    let r = ellR(dx, dy, dz, AX);
    const z = r * dz;
    const taper = 1 - 0.17 * smoothstep(20, 110, z) - 0.22 * smoothstep(-55, -120, z);
    if (taper !== 1) r = ellR(dx, dy, dz, AX * taper);
    if (dy < 0) r = smin(r, floorY(r * dz) / dy, 10);
    if (dx < 0) r = smin(r, MEDIAL / -dx, 6);
    return r;
}

// Principal sulci, as polylines in the lateral (z, y) projection, local frame.
const SYLVIAN = [[48, -28], [10, -14], [-30, 0], [-48, 15]];
const STS     = [[42, -42], [2, -29], [-38, -15], [-58, 0]];

function sylvianY(z) {
    const s = SYLVIAN;
    if (z >= s[0][0]) return s[0][1];
    for (let i = 0; i < s.length - 1; i++) {
        const [z0, y0] = s[i], [z1, y1] = s[i + 1];
        if (z <= z0 && z >= z1) return y0 + (y1 - y0) * (z - z0) / (z1 - z0);
    }
    return s[s.length - 1][1];
}

/** How deep the principal sulci cut at this surface point (local frame). */
export function majorSulciDepth(px, py, pz, dx) {
    const lat = smoothstep(0.02, 0.35, dx);          // lateral convexity only
    const dor = smoothstep(-0.4, -0.05, dx);         // everything but the medial face
    const ys = sylvianY(pz);
    const above = smoothstep(ys - 2, ys + 6, py);    // 1 above the lateral fissure
    let d = 0;
    d += 9.0 * lat * groove(distPolyline(pz, py, SYLVIAN), 3.0);
    d += 3.2 * lat * (1 - above) * groove(distPolyline(pz, py, STS), 2.2);
    d += 5.0 * dor * above * groove(distSeg(pz, py, -2, 76, 22, -10), 2.4);   // central
    d += 2.6 * dor * above * groove(distSeg(pz, py, 14, 76, 38, -8), 2.0);    // precentral
    d += 2.6 * dor * above * groove(distSeg(pz, py, -18, 74, 6, -6), 2.0);    // postcentral
    d += 2.8 * dor * above * groove(distSeg(pz, py, -14, 42, -88, 26), 2.0);  // intraparietal
    d += 2.4 * dor * groove(Math.abs(py - 38), 2.0)
               * smoothstep(38, 56, pz) * (1 - smoothstep(98, 110, pz));      // superior frontal
    d += 2.4 * lat * above * groove(Math.abs(py - 12), 2.0)
               * smoothstep(44, 62, pz) * (1 - smoothstep(92, 104, pz));      // inferior frontal
    return d;
}

/** Radius of the cortical surface nodes sit under: smooth shape + principal
 *  sulci, no fine folding (a neuron should not jitter with every gyrus). */
export function hemiRadius(dx, dy, dz) {
    const r = hemiBaseRadius(dx, dy, dz);
    return r - majorSulciDepth(dx * r, dy * r, dz * r, dx);
}

/**
 * Fine folding for the visible cortex. Sulci are the THIN lines — where the
 * warped noise crosses zero — and gyri are the broad crowns between them,
 * which is the right way round: a brain is mostly crown, cut by narrow cracks.
 * @returns {{disp: number, fold: number}} fold is 0 in a sulcus, 1 on a crown
 */
export function cortexFold(wx, wy, wz) {
    const f = 0.021;
    const wxp = noise3(wx * f, wy * f, wz * f);
    const wyp = noise3(wx * f + 31.7, wy * f + 11.3, wz * f - 7.1);
    const wzp = noise3(wx * f - 19.9, wy * f + 5.5, wz * f + 23.3);
    const q = 0.043;
    const n = noise3(wx * q + wxp * 1.1, wy * q + wyp * 1.1, wz * q + wzp * 1.1);
    const a = Math.abs(n);
    const sulcus = 1 - smoothstep(0.0, 0.17, a);
    const crown = smoothstep(0.12, 0.65, a);
    // A second, finer octave so the crowns are not perfectly smooth domes.
    const n2 = noise3(wx * 0.11 + 3.3, wy * 0.11, wz * 0.11 - 8.8);
    return { disp: -2.8 * sulcus + 0.9 * crown + 0.35 * n2, fold: 1 - sulcus };
}

// ── cerebellum, brainstem, and the deep landmarks fibres route through ──

export const CB_C = { x: 0, y: -40, z: -80 };
const CB_AX = 60, CB_AYT = 24, CB_AYB = 30, CB_AZ = 40;

/** Cerebellum radius around CB_C (world-aligned frame). Two lobes with a
 *  vermis notch at the back, flattened in front where the brainstem sits. */
export function cbBaseRadius(dx, dy, dz) {
    const ay = dy > 0 ? CB_AYT : CB_AYB;
    let r = 1 / Math.sqrt((dx / CB_AX) * (dx / CB_AX) + (dy / ay) * (dy / ay) + (dz / CB_AZ) * (dz / CB_AZ));
    const px = r * dx;
    r -= 5 * Math.exp(-(px / 9) * (px / 9)) * smoothstep(0.0, -0.8, dz);
    if (dz > 0) r = smin(r, 26 / dz, 6);
    return r;
}

/** Folia: the cerebellum's fine, transverse, parallel leaves. */
export function cerebellumFold(wx, wy, wz) {
    const a = Math.atan2(wy - CB_C.y, -(wz - CB_C.z));
    const wob = noise3(wx * 0.05, wy * 0.05, wz * 0.05) * 0.5;
    const s = Math.abs(Math.sin(a * 17 + wob));
    const sulcus = 1 - smoothstep(0.0, 0.3, s);
    return { disp: -1.9 * sulcus, fold: 1 - sulcus };
}

/** Brainstem centreline, midbrain → pons → medulla → cord. */
export const STEM_PATH = [
    { x: 0, y:   -4, z: -10 },
    { x: 0, y:  -22, z: -20 },
    { x: 0, y:  -42, z: -27 },
    { x: 0, y:  -62, z: -33 },
    { x: 0, y:  -84, z: -37 },
    { x: 0, y: -104, z: -39 },
];
/** Radius along STEM_PATH (t = 0..1): the pons is the bulge. */
export function stemRadius(t) {
    return 13 + 7 * Math.exp(-Math.pow((t - 0.34) / 0.14, 2)) - 5 * smoothstep(0.5, 1.0, t);
}

export const PONS = { x: 0, y: -38, z: -25 };
/** The corpus callosum as an arch over the midline, genu (front) to
 *  splenium (back). Callosal fibres cross the midline at this height. */
export function callosumY(z) {
    const t = (z + 2) / 40;
    return 14 + 9 * (1 - t * t);
}
export const CC_Z_MIN = -40, CC_Z_MAX = 38;

/** Where the scene frames the brain from by default: the classic left-lateral
 *  three-quarter view, frontal lobe on the viewer's left. */
export const BRAIN_CENTER = { x: 0, y: 12, z: -8 };
export const BRAIN_VIEW_DIR = { x: 0.88, y: 0.3, z: 0.36 };

// ── the atlas: which region a category lives in ─────────────────────────

/* Cortical regions, as a point near the surface in the lateral-positive
 * hemisphere frame. The direction from the hemisphere centre to this point is
 * the region's home; packing may nudge it so neighbours do not overlap. */
const CORTEX = {
    FRONTAL:       { p: [34, 40, 72],   label: 'Frontal lobe' },
    FRONTAL_POLE:  { p: [16, 14, 112],  label: 'Frontal pole' },
    ORBITO:        { p: [22, -26, 84],  label: 'Orbitofrontal cortex' },
    MOTOR:         { p: [30, 58, 18],   label: 'Motor cortex' },
    SOMATO:        { p: [32, 56, -8],   label: 'Somatosensory cortex' },
    PARIETAL_SUP:  { p: [24, 58, -48],  label: 'Superior parietal lobule' },
    PARIETAL_INF:  { p: [48, 26, -52],  label: 'Inferior parietal lobule' },
    TEMPORAL_SUP:  { p: [52, -14, -18], label: 'Superior temporal gyrus', labelL: "Wernicke's area" },
    TEMPORAL:      { p: [48, -40, 4],   label: 'Temporal lobe' },
    TEMPORAL_POLE: { p: [38, -36, 44],  label: 'Temporal pole' },
    OCCIPITAL:     { p: [26, 8, -116],  label: 'Visual cortex' },
    BROCA:         { p: [50, 2, 52],    label: 'Inferior frontal gyrus', labelL: "Broca's area" },
};

/* Deep structures: small volumes inside the cerebrum, placed by centre (x is
 * lateral, multiplied by the side's sign) and a basis. su/sv/sn stretch the
 * physics disk into the structure's shape. */
const DEEP = {
    AMYGDALA:     { c: [26, -28, 28],  u: [0, 0, 1],        v: [0, 1, 0], su: 1.0, sv: 0.8,  sn: 0.8,  label: 'Amygdala' },
    HIPPOCAMPUS:  { c: [29, -20, -6],  u: [0, 0.35, -1],    v: [0, 1, 0.35], su: 1.7, sv: 0.55, sn: 0.55, label: 'Hippocampus' },
    THALAMUS:     { c: [11, 4, -14],   u: [0, 0, 1],        v: [0, 1, 0], su: 1.3, sv: 0.8,  sn: 0.7,  label: 'Thalamus' },
    HYPOTHALAMUS: { c: [0, -8, 8],     u: [0, 0, 1],        v: [1, 0, 0], su: 1.0, sv: 0.8,  sn: 0.6,  label: 'Hypothalamus', midline: true },
    INSULA:       { c: [38, 2, 8],     u: [0, 0, 1],        v: [0, 1, 0], su: 1.2, sv: 0.8,  sn: 0.35, label: 'Insula' },
    CINGULATE:    { c: [6, 36, -2],    u: [0, 0, 1],        v: [0, 1, 0], su: 1.9, sv: 0.5,  sn: 0.3,  label: 'Cingulate gyrus' },
    BRAINSTEM:    { c: [0, -52, -33],  u: [0, -1, -0.3],    v: [1, 0, 0], su: 1.8, sv: 0.5,  sn: 0.5,  label: 'Brainstem', midline: true },
};

/* What each category's notes are, mapped onto what that part of a brain does.
 * Left hemisphere carries logic and language, the right one spatial and
 * visual work — a simplification, but the one people recognise. */
const CATEGORY_REGION = {
    Programming:        ['L', 'FRONTAL'],        // planning, executive logic
    AI_MachineLearning: ['R', 'FRONTAL'],        // higher cognition
    Design_Art:         ['R', 'OCCIPITAL'],      // vision
    DataScience:        ['L', 'PARIETAL_INF'],   // numbers (intraparietal)
    Blockchain_Web3:    ['R', 'PARIETAL_SUP'],   // spatial, distributed
    // Right inferior frontal gyrus: risk and response inhibition. Not the
    // orbitofrontal cortex, though that is the textbook "value" region — it is
    // on the underside, and a 90-note region nobody can see is a lost region.
    Business_Finance:   ['R', 'BROCA'],
    Engineering:        ['L', 'MOTOR'],          // building things
    Web_Development:    ['L', 'TEMPORAL_SUP'],   // language (Wernicke)
    Security_Crypto:    ['R', 'AMYGDALA'],       // threat detection
    Health_Medicine:    ['M', 'BRAINSTEM'],      // life support
    Health_Wellness:    ['M', 'HYPOTHALAMUS'],   // homeostasis
    GameDev:            ['R', 'CEREBELLUM'],     // timing, reflexes
    DevOps_Cloud:       ['L', 'CEREBELLUM'],     // automation
    Productivity:       ['L', 'CEREBELLUM'],     // habits
    Science:            ['R', 'PARIETAL_INF'],   // spatial reasoning
    Mathematics:        ['L', 'PARIETAL_SUP'],
    Education:          ['L', 'HIPPOCAMPUS'],    // memory, learning
    Philosophy:         ['R', 'FRONTAL_POLE'],   // reflection
    Lifestyle:          ['R', 'CINGULATE'],      // emotion
    Other:              ['R', 'INSULA'],
};
/** Homes for categories the map has never heard of, in the order handed out. */
const SPARE_REGIONS = [
    ['R', 'TEMPORAL'], ['L', 'TEMPORAL'], ['L', 'BROCA'], ['R', 'SOMATO'],
    ['L', 'OCCIPITAL'], ['R', 'TEMPORAL_POLE'], ['L', 'TEMPORAL_POLE'], ['L', 'SOMATO'],
    ['R', 'MOTOR'], ['R', 'BROCA'], ['L', 'ORBITO'], ['L', 'FRONTAL_POLE'],
    ['L', 'INSULA'], ['L', 'CINGULATE'], ['R', 'HIPPOCAMPUS'], ['L', 'AMYGDALA'],
    ['L', 'THALAMUS'], ['R', 'THALAMUS'], ['R', 'TEMPORAL_SUP'], ['L', 'PARIETAL_SUP'],
];

const signOf = (h) => (h === 'L' ? 1 : h === 'R' ? -1 : 0);

function regionLabelFor(hemi, key) {
    const side = hemi === 'L' ? ' · L' : hemi === 'R' ? ' · R' : '';
    if (key === 'CEREBELLUM') return 'Cerebellum' + side;
    const deep = DEEP[key];
    if (deep) return deep.label + (deep.midline ? '' : side);
    const c = CORTEX[key];
    if (!c) return key;
    return (hemi === 'L' && c.labelL) ? c.labelL : c.label + side;
}

// ── surfaces: one place that turns (surface, direction, depth) into world ─

/** Radius of a surface along a local unit direction. */
function surfaceRadius(surface, dx, dy, dz) {
    return surface === 'CB' ? cbBaseRadius(dx, dy, dz) : hemiRadius(dx, dy, dz);
}

/**
 * World position + outward normal for a point `depth` under a surface.
 * `sign` is the hemisphere's (+1 left, -1 right); the direction is in that
 * hemisphere's lateral-positive frame. For the cerebellum the frame is world.
 */
export function surfacePoint(surface, sign, dx, dy, dz, depth, outP, outN) {
    const r = surfaceRadius(surface, dx, dy, dz) - depth;
    if (surface === 'CB') {
        outP.x = CB_C.x + dx * r; outP.y = CB_C.y + dy * r; outP.z = CB_C.z + dz * r;
        outN.x = dx; outN.y = dy; outN.z = dz;
    } else {
        outP.x = sign * (HEMI_CX + dx * r); outP.y = HEMI_CY + dy * r; outP.z = HEMI_CZ + dz * r;
        outN.x = sign * dx; outN.y = dy; outN.z = dz;
    }
}

function tangentBasis(d) {
    const up = Math.abs(d.y) < 0.92 ? { x: 0, y: 1, z: 0 } : { x: 0, y: 0, z: 1 };
    const tu = norm3(cross(up, d));
    const tv = norm3(cross(d, tu));
    return { tu, tv };
}

/**
 * Project a region-local (u, v, nz) — what the physics integrates — to world.
 *
 * Surface regions use an exponential map: distance from the patch centre is
 * kept as distance ALONG the curved cortex, so a patch wraps the brain the
 * way a parcel of cortex does instead of sticking out as a flat disc. Volume
 * regions are plain stretched disks, which is what a deep nucleus is.
 */
export function projectBrainNode(g, u, v, nz, outP, outN) {
    if (g.kind === 'surface') {
        let dx = g.dir.x, dy = g.dir.y, dz = g.dir.z;
        const rho = Math.hypot(u, v);
        if (rho > 1e-6) {
            const a = rho / g.Rc, ca = Math.cos(a), sa = Math.sin(a);
            const tx = (u * g.tu.x + v * g.tv.x) / rho;
            const ty = (u * g.tu.y + v * g.tv.y) / rho;
            const tz = (u * g.tu.z + v * g.tv.z) / rho;
            dx = dx * ca + tx * sa; dy = dy * ca + ty * sa; dz = dz * ca + tz * sa;
            const l = Math.hypot(dx, dy, dz) || 1;
            dx /= l; dy /= l; dz /= l;
        }
        surfacePoint(g.surface, g.sign, dx, dy, dz, nz, outP, outN);
        return;
    }
    const su = u * g.su, sv = v * g.sv, sn = nz * g.sn;
    outP.x = g.center.x + g.basisU.x * su + g.basisV.x * sv + g.normal.x * sn;
    outP.y = g.center.y + g.basisU.y * su + g.basisV.y * sv + g.normal.y * sn;
    outP.z = g.center.z + g.basisU.z * su + g.basisV.z * sv + g.normal.z * sn;
    // A neuron in a nucleus has no cortex to face; it faces out of the nucleus.
    const ox = outP.x - g.center.x, oy = outP.y - g.center.y, oz = outP.z - g.center.z;
    const l = Math.hypot(ox, oy, oz);
    if (l > 1e-4) { outN.x = ox / l; outN.y = oy / l; outN.z = oz / l; }
    else { outN.x = g.normal.x; outN.y = g.normal.y; outN.z = g.normal.z; }
}

// ── packing: regions sharing a surface must not sit on top of each other ─

function packPatches(patches) {
    const bySurface = new Map();
    for (const p of patches) {
        const k = p.surface + (p.surface === 'CB' ? '' : p.sign);
        if (!bySurface.has(k)) bySurface.set(k, []);
        bySurface.get(k).push(p);
    }
    for (const list of bySurface.values()) {
        for (let it = 0; it < 220; it++) {
            for (const p of list) p.Rc = surfaceRadius(p.surface, p.dir.x, p.dir.y, p.dir.z);
            for (let i = 0; i < list.length; i++) {
                for (let j = i + 1; j < list.length; j++) {
                    const a = list[i], b = list[j];
                    const dot = clamp(a.dir.x * b.dir.x + a.dir.y * b.dir.y + a.dir.z * b.dir.z, -1, 1);
                    const ang = Math.acos(dot);
                    const need = (a.radius + b.radius) * 0.94 / ((a.Rc + b.Rc) / 2);
                    if (ang >= need) continue;
                    const push = (need - ang) * 0.5;
                    // The bigger region yields less — it is harder to move a lobe.
                    const wa = b.radius / (a.radius + b.radius), wb = 1 - wa;
                    // Direction on the sphere from b toward a (and back).
                    let ex = a.dir.x - b.dir.x * dot, ey = a.dir.y - b.dir.y * dot, ez = a.dir.z - b.dir.z * dot;
                    let el = Math.hypot(ex, ey, ez);
                    if (el < 1e-6) { ex = 0; ey = 1; ez = 0; el = 1; }
                    ex /= el; ey /= el; ez /= el;
                    a.dir.x += ex * push * wa * 2; a.dir.y += ey * push * wa * 2; a.dir.z += ez * push * wa * 2;
                    b.dir.x -= ex * push * wb * 2; b.dir.y -= ey * push * wb * 2; b.dir.z -= ez * push * wb * 2;
                    norm3(a.dir); norm3(b.dir);
                }
            }
            for (const p of list) {
                // Home spring: a region drifts only as far as its neighbours
                // insist, then comes back toward where its function lives.
                p.dir.x += (p.home.x - p.dir.x) * 0.035;
                p.dir.y += (p.home.y - p.dir.y) * 0.035;
                p.dir.z += (p.home.z - p.dir.z) * 0.035;
                if (p.surface !== 'CB') {
                    // Off the medial face and the very floor: a region placed
                    // there would be a parcel nobody can see from outside.
                    if (p.dir.x < -0.2) p.dir.x = -0.2;
                    if (p.dir.y < -0.72) p.dir.y = -0.72;
                } else {
                    if (p.dir.z > 0.25) p.dir.z = 0.25;
                    if (p.dir.y > 0.35) p.dir.y = 0.35;
                }
                norm3(p.dir);
            }
        }
    }
}

// ── the graph ────────────────────────────────────────────────────────────

const SURFACE_R = (n) => 6 + 2.7 * Math.sqrt(n);
const VOLUME_R = (n) => 4 + 2.4 * Math.sqrt(n);
const GOLDEN = Math.PI * (3 - Math.sqrt(5));

/**
 * Build the neural brain from a brain-export payload.
 * Same contract as buildUniverse: { nodes, edges, galaxies }, where every
 * "galaxy" is a brain region carrying the extra fields projectBrainNode reads.
 */
export function buildBrainGraph(brain) {
    const rawNodes = brain.Nodes ?? brain.nodes ?? [];
    if (!rawNodes.length) return { nodes: [], edges: [], galaxies: [] };

    const byCat = new Map();
    for (const n of rawNodes) {
        const cat = n.PrimaryCategory ?? n.primaryCategory ?? 'Other';
        if (!byCat.has(cat)) byCat.set(cat, []);
        byCat.get(cat).push(n);
    }
    const sortedCats = [...byCat.entries()].sort((a, b) => b[1].length - a[1].length);
    const expertiseByCat = expertiseByCategory(brain);

    // 1) Every category gets a home. Mapped ones first, so an unknown category
    //    can never take the region a known one was promised.
    const used = new Set();
    const homeOf = new Map();
    for (const [cat] of sortedCats) {
        const m = CATEGORY_REGION[cat];
        if (m) { homeOf.set(cat, m); used.add(m.join(':')); }
    }
    let spare = 0;
    for (const [cat] of sortedCats) {
        if (homeOf.has(cat)) continue;
        let pick = null;
        for (let k = 0; k < SPARE_REGIONS.length; k++) {
            const s = SPARE_REGIONS[(spare + k) % SPARE_REGIONS.length];
            if (!used.has(s.join(':'))) { pick = s; spare = (spare + k + 1) % SPARE_REGIONS.length; break; }
        }
        // More categories than regions: share one, and let packing split it.
        if (!pick) { pick = SPARE_REGIONS[spare % SPARE_REGIONS.length]; spare++; }
        used.add(pick.join(':'));
        homeOf.set(cat, pick);
    }

    // 2) Regions.
    const galaxies = sortedCats.map(([category, list]) => {
        const pal = paletteFor(category);
        const ex = expertiseByCat.get(category) ?? { score: 0, noteCount: list.length, totalWords: 0, growthRate: 0 };
        const [hemi, key] = homeOf.get(category);
        const sign = signOf(hemi) || 1;
        const g = {
            category,
            label: pal.label,
            color: pal.hex,
            count: list.length,
            score: ex.score,
            totalWords: ex.totalWords,
            growthRate: ex.growthRate,
            regionKey: key,
            regionLabel: regionLabelFor(hemi, key),
            hemi,
            sign,
        };
        if (CORTEX[key] || key === 'CEREBELLUM') {
            g.kind = 'surface';
            g.radius = SURFACE_R(list.length);
            if (key === 'CEREBELLUM') {
                g.surface = 'CB';
                g.home = norm3({ x: sign * 34, y: -8, z: -30 });
            } else {
                g.surface = hemi === 'R' ? 'R' : 'L';
                const [px, py, pz] = CORTEX[key].p;     // already hemisphere-local
                g.home = norm3({ x: px, y: py, z: pz });
            }
            g.dir = { ...g.home };
        } else {
            const d = DEEP[key];
            g.kind = 'volume';
            g.radius = VOLUME_R(list.length);
            g.center = { x: d.midline ? 0 : sign * d.c[0], y: d.c[1], z: d.c[2] };
            g.basisU = norm3({ x: sign * d.u[0], y: d.u[1], z: d.u[2] });
            const v0 = norm3({ x: sign * d.v[0], y: d.v[1], z: d.v[2] });
            // Orthonormalise v against u, then n = u × v.
            const dv = v0.x * g.basisU.x + v0.y * g.basisU.y + v0.z * g.basisU.z;
            g.basisV = norm3({ x: v0.x - g.basisU.x * dv, y: v0.y - g.basisU.y * dv, z: v0.z - g.basisU.z * dv });
            g.normal = norm3(cross(g.basisU, g.basisV));
            g.su = d.su; g.sv = d.sv; g.sn = d.sn;
        }
        return g;
    });

    // 3) Slide surface regions apart, then freeze each one's frame.
    packPatches(galaxies.filter(g => g.kind === 'surface'));
    const tmpP = { x: 0, y: 0, z: 0 }, tmpN = { x: 0, y: 0, z: 0 };
    for (const g of galaxies) {
        if (g.kind !== 'surface') continue;
        g.Rc = surfaceRadius(g.surface, g.dir.x, g.dir.y, g.dir.z);
        const { tu, tv } = tangentBasis(g.dir);
        g.tu = tu; g.tv = tv;
        surfacePoint(g.surface, g.sign, g.dir.x, g.dir.y, g.dir.z, 0, tmpP, tmpN);
        g.center = { ...tmpP };
        g.normal = norm3({ ...tmpN });
        // World-frame tangents, for anything that wants the scene's disk API.
        const wt = (t) => (g.surface === 'CB' ? { ...t } : { x: g.sign * t.x, y: t.y, z: t.z });
        g.basisU = wt(tu); g.basisV = wt(tv);
    }

    // 4) Neurons. A sunflower spiral inside each region's disk, most
    //    important notes nearest the centre — the region's bright core.
    const nodes = [];
    const idIndex = new Map();
    galaxies.forEach((g, galaxyIdx) => {
        const list = byCat.get(g.category);
        const imp = (n) => n.Importance ?? n.importance ?? 1;
        const ordered = list.map((n, k) => ({ n, k })).sort((a, b) => imp(b.n) - imp(a.n) || a.k - b.k);
        const importanceMax = Math.max(...list.map(imp), 1);
        const count = ordered.length;
        ordered.forEach(({ n }, rank) => {
            const id = n.Id ?? n.id ?? `${g.category}-${rank}`;
            const r = rng(hashStr(id));
            const rho = g.radius * 0.93 * Math.sqrt((rank + 0.5) / count);
            const theta = rank * GOLDEN + (r() - 0.5) * 0.35;
            const u = rho * Math.cos(theta), v = rho * Math.sin(theta);
            // Surface: depth into the cortical layer (0.8 .. 6.5). Volume: a
            // gaussian spread through the nucleus.
            const nz = g.kind === 'surface'
                ? 0.8 + Math.pow(r(), 1.4) * 5.7
                : gauss(r) * g.radius * 0.32;
            const position = { x: 0, y: 0, z: 0 }, normal = { x: 0, y: 1, z: 0 };
            projectBrainNode(g, u, v, nz, position, normal);

            const importance = imp(n) / importanceMax;
            const wc = Math.max(1, n.WordCount ?? n.wordCount ?? 1);
            const node = {
                id,
                title: n.Title ?? n.title ?? '(untitled)',
                category: g.category,
                galaxyIdx,
                categoryLabel: `${g.label} · ${g.regionLabel}`,
                color: g.color,
                position,
                normal,
                local: { u, v, n: nz },
                // Which body of tissue the neuron sits in — fibres route by it.
                surf: g.kind === 'surface' ? g.surface : 'V',
                side: g.kind === 'surface'
                    ? (g.surface === 'CB' ? Math.sign(position.x) || 1 : g.sign)
                    : Math.sign(position.x),
                size: 0.55 + Math.log10(wc) * 0.7,
                brightness: 0.45 + importance * 0.55,
                wordCount: wc,
                tags: n.Tags ?? n.tags ?? [],
                preview: (n.Preview ?? n.preview ?? '').slice(0, 480),
                modifiedAt: n.ModifiedAt ?? n.modifiedAt ?? null,
                linkedIds: n.LinkedNodeIds ?? n.linkedNodeIds ?? [],
            };
            idIndex.set(id, nodes.length);
            nodes.push(node);
        });
    });

    return { nodes, edges: linkEdges(nodes, idIndex), galaxies };
}

// ── fibres ───────────────────────────────────────────────────────────────

/**
 * Control points for the fibre joining two neurons, written into `out`
 * (an array of at least 5 {x,y,z}). Returns how many were written — 4 means
 * a cubic Bézier, 5 means a spline through a crossing point.
 *
 *   same hemisphere     dives under the cortex and comes back up: short
 *                       links become U-fibres, long ones association tracts
 *   across the midline  crosses through the corpus callosum
 *   to the cerebellum   runs through the pons, as cerebellar input does
 *   into a nucleus      dives from the cortex into the deep structure
 */
export function fiberRoute(A, B, out) {
    const pa = A.position, pb = B.position, na = A.normal, nb = B.normal;
    const dist = Math.hypot(pb.x - pa.x, pb.y - pa.y, pb.z - pa.z);
    const set = (i, x, y, z) => { out[i].x = x; out[i].y = y; out[i].z = z; };
    set(0, pa.x, pa.y, pa.z);

    const cortexA = A.surf === 'L' || A.surf === 'R';
    const cortexB = B.surf === 'L' || B.surf === 'R';
    const cbA = A.surf === 'CB', cbB = B.surf === 'CB';

    if ((cortexA && cortexB && A.side !== B.side)) {
        const zc = clamp((pa.z + pb.z) / 2, CC_Z_MIN + 2, CC_Z_MAX - 2);
        const da = clamp(10 + dist * 0.12, 10, 34), db = da;
        set(1, pa.x - na.x * da, pa.y - na.y * da, pa.z - na.z * da);
        set(2, 0, callosumY(zc), zc);
        set(3, pb.x - nb.x * db, pb.y - nb.y * db, pb.z - nb.z * db);
        set(4, pb.x, pb.y, pb.z);
        return 5;
    }
    if (cbA !== cbB && (cbA || cbB)) {
        const d = clamp(6 + dist * 0.1, 6, 24);
        set(1, pa.x - na.x * d, pa.y - na.y * d, pa.z - na.z * d);
        set(2, PONS.x + (pa.x + pb.x) * 0.05, PONS.y, PONS.z);
        set(3, pb.x - nb.x * d, pb.y - nb.y * d, pb.z - nb.z * d);
        set(4, pb.x, pb.y, pb.z);
        return 5;
    }
    const d = clamp(5 + dist * 0.3, 5, 42);
    if (A.surf === 'V' || B.surf === 'V') {
        // Into a nucleus: dive from the cortical end, arrive straight-ish.
        const c = A.surf === 'V' ? B : A;
        const k = c === A ? 1 : 2;
        const pc = c.position, nc = c.normal;
        const dx = pc.x - nc.x * d, dy = pc.y - nc.y * d, dz = pc.z - nc.z * d;
        if (k === 1) {
            set(1, dx, dy, dz);
            set(2, pb.x + (dx - pb.x) * 0.3, pb.y + (dy - pb.y) * 0.3, pb.z + (dz - pb.z) * 0.3);
        } else {
            set(1, pa.x + (dx - pa.x) * 0.3, pa.y + (dy - pa.y) * 0.3, pa.z + (dz - pa.z) * 0.3);
            set(2, dx, dy, dz);
        }
        set(3, pb.x, pb.y, pb.z);
        return 4;
    }
    set(1, pa.x - na.x * d, pa.y - na.y * d, pa.z - na.z * d);
    set(2, pb.x - nb.x * d, pb.y - nb.y * d, pb.z - nb.z * d);
    set(3, pb.x, pb.y, pb.z);
    return 4;
}

/**
 * Sample a route into `S + 1` points written at `dst[off..]` (xyz triples).
 * 4 points → cubic Bézier (smooth dive that never quite touches bottom);
 * 5 points → centripetal-ish Catmull-Rom through the crossing point.
 */
export function sampleRoute(pts, count, S, dst, off) {
    if (count === 4) {
        const [p0, p1, p2, p3] = pts;
        for (let i = 0; i <= S; i++) {
            const t = i / S, mt = 1 - t;
            const a = mt * mt * mt, b = 3 * mt * mt * t, c = 3 * mt * t * t, d = t * t * t;
            dst[off++] = a * p0.x + b * p1.x + c * p2.x + d * p3.x;
            dst[off++] = a * p0.y + b * p1.y + c * p2.y + d * p3.y;
            dst[off++] = a * p0.z + b * p1.z + c * p2.z + d * p3.z;
        }
        return off;
    }
    const segs = count - 1;
    for (let i = 0; i <= S; i++) {
        const f = (i / S) * segs;
        const k = Math.min(segs - 1, Math.floor(f));
        const t = f - k;
        const P0 = pts[Math.max(0, k - 1)], P1 = pts[k], P2 = pts[k + 1], P3 = pts[Math.min(count - 1, k + 2)];
        const t2 = t * t, t3 = t2 * t;
        const c0 = -0.5 * t3 + t2 - 0.5 * t;
        const c1 = 1.5 * t3 - 2.5 * t2 + 1;
        const c2 = -1.5 * t3 + 2 * t2 + 0.5 * t;
        const c3 = 0.5 * t3 - 0.5 * t2;
        dst[off++] = c0 * P0.x + c1 * P1.x + c2 * P2.x + c3 * P3.x;
        dst[off++] = c0 * P0.y + c1 * P1.y + c2 * P2.y + c3 * P3.y;
        dst[off++] = c0 * P0.z + c1 * P1.z + c2 * P2.z + c3 * P3.z;
    }
    return off;
}

// ── decor: what makes the brain read as a brain before a single note ────

/** Uniformly random unit vector. */
function randDir(rand) {
    const z = rand() * 2 - 1, a = rand() * Math.PI * 2, s = Math.sqrt(1 - z * z);
    return { x: s * Math.cos(a), y: s * Math.sin(a), z };
}

/**
 * The cortex's own neurons — the ones that are not notes. Thousands of faint
 * points in the grey-matter layer, tinted where a region lives, so the brain
 * has its shape and its parcellation even where the vault is thin.
 * @returns {{positions: Float32Array, colors: Float32Array}}
 */
export function gliaPoints(count, galaxies, seed = 7) {
    const rand = rng(seed);
    const positions = new Float32Array(count * 3);
    const colors = new Float32Array(count * 3);
    const P = { x: 0, y: 0, z: 0 }, N = { x: 0, y: 0, z: 0 };
    const surfaces = galaxies.filter(g => g.kind === 'surface');
    for (let i = 0; i < count; i++) {
        const pick = rand();
        const d = randDir(rand);
        let surface, sign;
        if (pick < 0.86) { surface = pick < 0.43 ? 'L' : 'R'; sign = surface === 'L' ? 1 : -1; }
        else { surface = 'CB'; sign = 1; }
        const depth = Math.pow(rand(), 1.6) * 6.5;
        surfacePoint(surface, sign, d.x, d.y, d.z, depth, P, N);
        positions[i * 3] = P.x; positions[i * 3 + 1] = P.y; positions[i * 3 + 2] = P.z;

        // Pale tissue by default; the region's hue where one lives.
        let r = 0.52, g = 0.6, b = 0.95;
        for (const reg of surfaces) {
            if (reg.surface !== surface || (surface !== 'CB' && reg.sign !== sign)) continue;
            const dot = d.x * reg.dir.x + d.y * reg.dir.y + d.z * reg.dir.z;
            const ang = Math.acos(clamp(dot, -1, 1));
            const lim = reg.radius / reg.Rc;
            if (ang < lim) {
                const k = 0.62 * (1 - smoothstep(lim * 0.6, lim, ang));
                r += (((reg.color >> 16) & 255) / 255 - r) * k;
                g += (((reg.color >> 8) & 255) / 255 - g) * k;
                b += ((reg.color & 255) / 255 - b) * k;
                break;
            }
        }
        const bright = 0.28 + rand() * 0.5;
        colors[i * 3] = r * bright; colors[i * 3 + 1] = g * bright; colors[i * 3 + 2] = b * bright;
    }
    return { positions, colors };
}

/**
 * White-matter tracts that are not links — the brain's own wiring. Without
 * them a small vault is a handful of lines in a glass shell; with them the
 * picture is tractography, and the vault's fibres are part of it.
 *
 * Returns fibres as sampled polylines, `S + 1` points each, back to back.
 * @returns {{points: Float32Array, fibers: number, S: number}}
 */
export function tractPolylines(seed = 11, S = 14) {
    const rand = rng(seed);
    const P = { x: 0, y: 0, z: 0 }, N = { x: 0, y: 0, z: 0 };
    const pts = [0, 1, 2, 3, 4].map(() => ({ x: 0, y: 0, z: 0 }));
    const fibers = [];

    const cortexPoint = (sign, filter, depth = 4) => {
        for (let k = 0; k < 40; k++) {
            const d = randDir(rand);
            if (!filter(d)) continue;
            surfacePoint(sign > 0 ? 'L' : 'R', sign, d.x, d.y, d.z, depth, P, N);
            return { p: { ...P }, n: { ...N } };
        }
        const d = { x: 0.6, y: 0.6, z: 0 }; norm3(d);
        surfacePoint(sign > 0 ? 'L' : 'R', sign, d.x, d.y, d.z, depth, P, N);
        return { p: { ...P }, n: { ...N } };
    };
    const push = (count) => {
        const out = new Float32Array((S + 1) * 3);
        sampleRoute(pts, count, S, out, 0);
        fibers.push(out);
    };
    const put = (i, p) => { pts[i].x = p.x; pts[i].y = p.y; pts[i].z = p.z; };

    // Corpus callosum: dorsal cortex to dorsal cortex, arching over the midline.
    for (let i = 0; i < 420; i++) {
        const zc = CC_Z_MIN + rand() * (CC_Z_MAX - CC_Z_MIN);
        const band = (d) => d.y > 0.05 && d.x > -0.25 && Math.abs(d.z * 118 - zc * 1.4) < 60;
        const a = cortexPoint(1, band), b = cortexPoint(-1, band);
        put(0, a.p);
        put(1, { x: a.p.x - a.n.x * 18, y: a.p.y - a.n.y * 18, z: a.p.z - a.n.z * 18 });
        put(2, { x: 0, y: callosumY(zc), z: zc });
        put(3, { x: b.p.x - b.n.x * 18, y: b.p.y - b.n.y * 18, z: b.p.z - b.n.z * 18 });
        put(4, b.p);
        push(5);
    }
    // Corona radiata: brainstem up through the internal capsule, fanning out.
    for (let i = 0; i < 380; i++) {
        const sign = rand() < 0.5 ? 1 : -1;
        const a = cortexPoint(sign, (d) => d.y > -0.2 && d.x > -0.1);
        put(0, { x: (rand() - 0.5) * 8, y: -34 - rand() * 12, z: -24 - rand() * 6 });
        put(1, { x: sign * (8 + rand() * 4), y: -12, z: -16 + (rand() - 0.5) * 8 });
        put(2, { x: sign * (18 + rand() * 6), y: 8 + rand() * 6, z: -6 + (a.p.z + 4) * 0.35 });
        put(3, { x: a.p.x - a.n.x * 14, y: a.p.y - a.n.y * 14, z: a.p.z - a.n.z * 14 });
        put(4, a.p);
        push(5);
    }
    // Arcuate fasciculus: frontal lobe round the back of the lateral fissure
    // into the temporal lobe — the language loop.
    for (let i = 0; i < 240; i++) {
        const sign = rand() < 0.5 ? 1 : -1;
        const a = cortexPoint(sign, (d) => d.z > 0.3 && d.y > -0.15 && d.y < 0.45 && d.x > 0.2);
        const b = cortexPoint(sign, (d) => d.z < 0.25 && d.z > -0.35 && d.y < -0.25 && d.x > 0.2);
        put(0, a.p);
        put(1, { x: a.p.x - a.n.x * 16, y: a.p.y - a.n.y * 16, z: a.p.z - a.n.z * 16 });
        put(2, { x: sign * (HEMI_CX + 18), y: HEMI_CY + 8 + rand() * 8, z: -50 - rand() * 12 });
        put(3, { x: b.p.x - b.n.x * 16, y: b.p.y - b.n.y * 16, z: b.p.z - b.n.z * 16 });
        put(4, b.p);
        push(5);
    }
    // Cingulum: along the medial wall above the callosum, front to back.
    for (let i = 0; i < 160; i++) {
        const sign = rand() < 0.5 ? 1 : -1;
        const x = sign * (7 + rand() * 7);
        const z0 = 70 + rand() * 20, z1 = -70 - rand() * 25;
        put(0, { x, y: 12 + rand() * 12, z: z0 });
        put(1, { x, y: 30 + rand() * 6, z: z0 * 0.45 });
        put(2, { x, y: 36 + rand() * 5, z: -4 });
        put(3, { x, y: 28 + rand() * 6, z: z1 * 0.6 });
        put(4, { x, y: 4 + rand() * 12, z: z1 });
        push(5);
    }
    // Cerebellar peduncles: pons to the cerebellar cortex.
    for (let i = 0; i < 200; i++) {
        const d = randDir(rand);
        if (d.z > 0.1) d.z = -d.z;
        norm3(d);
        surfacePoint('CB', 1, d.x, d.y, d.z, 3.5, P, N);
        put(0, { x: (rand() - 0.5) * 10, y: PONS.y + (rand() - 0.5) * 10, z: PONS.z - 4 });
        put(1, { x: Math.sign(P.x) * 14, y: PONS.y - 4, z: -48 });
        put(2, { x: P.x - N.x * 10, y: P.y - N.y * 10, z: P.z - N.z * 10 });
        put(3, { ...P });
        push(4);
    }
    // Optic radiation: thalamus back to the visual cortex.
    for (let i = 0; i < 140; i++) {
        const sign = rand() < 0.5 ? 1 : -1;
        const a = cortexPoint(sign, (d) => d.z < -0.82);
        put(0, { x: sign * (12 + rand() * 4), y: 2 + rand() * 4, z: -22 });
        put(1, { x: sign * (32 + rand() * 6), y: -4 + rand() * 6, z: -46 });
        put(2, { x: a.p.x - a.n.x * 14, y: a.p.y - a.n.y * 14, z: a.p.z - a.n.z * 14 });
        put(3, a.p);
        push(4);
    }

    const points = new Float32Array(fibers.length * (S + 1) * 3);
    fibers.forEach((f, i) => points.set(f, i * (S + 1) * 3));
    return { points, fibers: fibers.length, S };
}

// ── geometry for the shells, as plain arrays ────────────────────────────

/** Indexed icosphere: `detail` subdivisions of an icosahedron, unit radius. */
export function icosphere(detail) {
    const t = (1 + Math.sqrt(5)) / 2;
    const v = [
        [-1, t, 0], [1, t, 0], [-1, -t, 0], [1, -t, 0],
        [0, -1, t], [0, 1, t], [0, -1, -t], [0, 1, -t],
        [t, 0, -1], [t, 0, 1], [-t, 0, -1], [-t, 0, 1],
    ].map(([x, y, z]) => { const l = Math.hypot(x, y, z); return [x / l, y / l, z / l]; });
    let f = [
        [0, 11, 5], [0, 5, 1], [0, 1, 7], [0, 7, 10], [0, 10, 11],
        [1, 5, 9], [5, 11, 4], [11, 10, 2], [10, 7, 6], [7, 1, 8],
        [3, 9, 4], [3, 4, 2], [3, 2, 6], [3, 6, 8], [3, 8, 9],
        [4, 9, 5], [2, 4, 11], [6, 2, 10], [8, 6, 7], [9, 8, 1],
    ];
    for (let s = 0; s < detail; s++) {
        const cache = new Map();
        const mid = (a, b) => {
            const key = a < b ? a * 1e6 + b : b * 1e6 + a;
            let m = cache.get(key);
            if (m !== undefined) return m;
            const [ax, ay, az] = v[a], [bx, by, bz] = v[b];
            let x = ax + bx, y = ay + by, z = az + bz;
            const l = Math.hypot(x, y, z);
            v.push([x / l, y / l, z / l]);
            m = v.length - 1;
            cache.set(key, m);
            return m;
        };
        const nf = [];
        for (const [a, b, c] of f) {
            const ab = mid(a, b), bc = mid(b, c), ca = mid(c, a);
            nf.push([a, ab, ca], [b, bc, ab], [c, ca, bc], [ab, bc, ca]);
        }
        f = nf;
    }
    const positions = new Float32Array(v.length * 3);
    v.forEach(([x, y, z], i) => { positions[i * 3] = x; positions[i * 3 + 1] = y; positions[i * 3 + 2] = z; });
    const index = new Uint32Array(f.length * 3);
    f.forEach(([a, b, c], i) => { index[i * 3] = a; index[i * 3 + 1] = b; index[i * 3 + 2] = c; });
    return { positions, index };
}

/**
 * Displace a unit icosphere into a hemisphere or the cerebellum.
 * `medial` marks the flat inner wall of a hemisphere (1 = fully medial): two
 * of those face each other across the fissure, and seen edge-on their rims
 * add up into one white sheet down the middle of the brain unless the shader
 * is told which faces they are.
 * @param {'L'|'R'|'CB'} which
 * @returns {{positions: Float32Array, fold: Float32Array, medial: Float32Array, index: Uint32Array}}
 */
export function shellGeometry(which, detail = 6, scale = 1) {
    const { positions: unit, index } = icosphere(detail);
    const n = unit.length / 3;
    const positions = new Float32Array(n * 3);
    const fold = new Float32Array(n);
    const medial = new Float32Array(n);
    const P = { x: 0, y: 0, z: 0 }, N = { x: 0, y: 0, z: 0 };
    const sign = which === 'R' ? -1 : 1;
    for (let i = 0; i < n; i++) {
        const dx = unit[i * 3], dy = unit[i * 3 + 1], dz = unit[i * 3 + 2];
        let f;
        if (which === 'CB') {
            surfacePoint('CB', 1, dx, dy, dz, 0, P, N);
            f = cerebellumFold(P.x, P.y, P.z);
        } else {
            const r0 = hemiBaseRadius(dx, dy, dz);
            const sulci = majorSulciDepth(dx * r0, dy * r0, dz * r0, dx);
            surfacePoint(which, sign, dx, dy, dz, 0, P, N);
            f = cortexFold(P.x, P.y, P.z);
            // The principal sulci are folds too — shade them like one.
            f = { disp: f.disp, fold: Math.min(f.fold, 1 - clamp(sulci / 6, 0, 1)) };
            // Less folding on the flat medial wall, which is mostly hidden.
            if (dx < -0.3) f.disp *= 0.5;
            medial[i] = smoothstep(-0.25, -0.6, dx);
        }
        positions[i * 3]     = (P.x + N.x * f.disp) * scale;
        positions[i * 3 + 1] = (P.y + N.y * f.disp) * scale;
        positions[i * 3 + 2] = (P.z + N.z * f.disp) * scale;
        fold[i] = f.fold;
    }
    return { positions, fold, medial, index };
}

// ── neurons up close (option C: zoom in and the dots grow dendrites) ────

/**
 * A procedural neuron: line segments in a local frame where +y points out of
 * the cortex. Seeded, so each note keeps its own tree.
 *
 *   pyramidal  apical dendrite climbing toward the surface with a tuft,
 *              basal dendrites skirting the soma, an axon diving into the
 *              white matter — the cortex's signature cell
 *   stellate   dendrites in every direction — deep nuclei
 *   purkinje   a flat espalier fan — the cerebellum's cell
 *
 * @returns {{pos: Float32Array, order: Float32Array, axon: Float32Array}}
 */
export function neuronTemplate(kind, seed) {
    const rand = rng(seed);
    const pos = [], order = [], axon = [];
    const seg = (a, b, o, ax) => {
        pos.push(a.x, a.y, a.z, b.x, b.y, b.z);
        order.push(o, o);
        axon.push(ax, ax);
    };
    const jitter = (d, amt, planar) => norm3({
        x: d.x + (rand() - 0.5) * amt,
        y: d.y + (rand() - 0.5) * amt,
        z: planar ? d.z * 0.3 : d.z + (rand() - 0.5) * amt,
    });
    const perp = (d, planar) => {
        const r = planar ? { x: rand() - 0.5, y: 0, z: 0 } : randDir(rand);
        const c = cross(d, r);
        return norm3(Math.hypot(c.x, c.y, c.z) < 1e-4 ? { x: 1, y: 0, z: 0 } : c);
    };
    let budget = 900;   // hard ceiling on segments, whatever the dice say

    function grow(p, d, len, ord, maxOrd, forkP, bias, isAxon, planar = false) {
        const steps = Math.max(2, Math.round(len / 0.55));
        const step = len / steps;
        let cur = p, dir = d;
        for (let s = 0; s < steps && budget > 0; s++) {
            dir = jitter({ x: dir.x + bias.x, y: dir.y + bias.y, z: dir.z + bias.z }, 0.45, planar);
            const nxt = { x: cur.x + dir.x * step, y: cur.y + dir.y * step, z: cur.z + dir.z * step };
            seg(cur, nxt, ord / (maxOrd + 1), isAxon ? 1 : 0);
            budget--;
            cur = nxt;
            if (ord < maxOrd && s > 0 && s < steps - 1 && rand() < forkP) {
                const side = perp(dir, planar);
                grow(cur, norm3({ x: dir.x * 0.6 + side.x * 0.8, y: dir.y * 0.6 + side.y * 0.8, z: dir.z * 0.6 + side.z * 0.8 }),
                     len * (0.35 + rand() * 0.25), ord + 1, maxOrd, forkP * 0.8, bias, isAxon, planar);
            }
        }
        if (ord < maxOrd && budget > 0) {
            for (let k = 0; k < 2; k++) {
                const side = perp(dir, planar);
                const sgn = k === 0 ? 1 : -1;
                grow(cur, norm3({ x: dir.x + side.x * 0.9 * sgn, y: dir.y + side.y * 0.9 * sgn, z: dir.z + side.z * 0.9 * sgn }),
                     len * (0.42 + rand() * 0.2), ord + 1, maxOrd, forkP * 0.7, bias, isAxon, planar);
            }
        }
    }

    const O = { x: 0, y: 0, z: 0 };
    const none = { x: 0, y: 0, z: 0 };
    if (kind === 'purkinje') {
        grow({ x: 0, y: 0.3, z: 0 }, { x: 0, y: 1, z: 0 }, 2.4, 0, 5, 0.3, { x: 0, y: 0.08, z: 0 }, false, true);
        grow({ x: 0, y: -0.3, z: 0 }, { x: 0, y: -1, z: 0 }, 9, 0, 1, 0.1, { x: 0, y: -0.06, z: 0 }, true);
    } else if (kind === 'stellate') {
        const k = 7 + Math.floor(rand() * 3);
        for (let i = 0; i < k; i++) grow(O, randDir(rand), 3.2 + rand() * 1.8, 1, 3, 0.22, none, false);
        grow(O, randDir(rand), 7, 0, 1, 0.12, none, true);
    } else {
        // Pyramidal: apical + tuft, basal skirt, axon.
        grow({ x: 0, y: 0.45, z: 0 }, { x: 0, y: 1, z: 0 }, 7.5 + rand() * 2, 1, 3, 0.26, { x: 0, y: 0.14, z: 0 }, false);
        const basal = 5 + Math.floor(rand() * 3);
        for (let i = 0; i < basal; i++) {
            const a = (i / basal) * Math.PI * 2 + rand() * 0.6;
            const d = norm3({ x: Math.cos(a), y: -0.35 + rand() * 0.5, z: Math.sin(a) });
            grow(O, d, 2.6 + rand() * 1.6, 1, 3, 0.2, { x: 0, y: -0.03, z: 0 }, false);
        }
        grow({ x: 0, y: -0.45, z: 0 }, { x: 0, y: -1, z: 0 }, 10, 0, 1, 0.14, { x: 0, y: -0.1, z: 0 }, true);
    }
    return { pos: new Float32Array(pos), order: new Float32Array(order), axon: new Float32Array(axon) };
}
