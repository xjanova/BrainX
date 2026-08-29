// face.js — the assistant's head: a parametric skull rendered as a wireframe,
// and a face that moves with what she says.
//
// WHY A CLOSED HEAD AND NOT A MASK. The first version was a height field,
// z = f(x, y) over a flat grid. A height field can only ever be a mask: it has
// no back, no ears, and — because z is a single value per (x, y) — no
// undercuts, so nostrils, the tuck under the chin, and the sweep of the jaw
// back to the ear are all literally unrepresentable. It read as a bent sheet
// the moment the head turned. This builds a CLOSED surface in (angle, height)
// instead: every horizontal slice is a superellipse whose width and (separate)
// front and back depths come from profile tables, so the silhouette is a skull
// from any angle, and features are free to push out, cut in, or fold back.
//
// WHY PROFILE TABLES AND NOT FORMULAE. The proportions are the likeness. Eyes
// at half the head's height, ears spanning brow to nose base, the widest point
// above and behind the ear, the chin narrower than the jaw — these are numbers
// a sculptor memorises, and they are far easier to get right, and to tune, as a
// table of control points than as nested curves. The tables below are in
// head-height units: 0 is the chin, 1 is the crown.
//
// WHY A LATHE PLUS FEATURES, NOT A LATHE ALONE. A single width-per-height
// cannot say "narrow at the front, wide at the sides" — and at chin level that
// is exactly the truth, because at the sides you are already in the neck. So
// the lathe carries the NECK through the lower rows and the whole mandible —
// chin, jawline, gonial angle — hangs off the front of it as a feature. Trying
// to encode the chin in the width table instead put a pinch right round the
// throat.
//
// WHY THE WIRES FADE. Drawing every edge of a closed head shows you the back
// of the skull through the front, and 5,800 segments of that is mush. Alpha
// comes from a shader instead: how much the surface faces you, how far away it
// is, and a rim term at the silhouette. Wires that face away nearly vanish and
// the outline glows, which reads as a hologram rather than as a bug.
//
// WHY THE AUDIO IS A FILE AND NOT speechSynthesis. This machine has no Thai
// voice at the OS level (SAPI and WinRT expose David / Zira / Mark, all
// en-US), and the WebView2 reads that same empty list. Thai TTS therefore
// happens server-side via `brainx-mcp speak`, and this module is handed an
// mp3 URL. Nothing here synthesises.
//
// WHY BANDS AND NOT AMPLITUDE. Driving a jaw from loudness gives a puppet that
// flaps with the volume — recognisably wrong, because mouth SHAPE is set by
// formants, not level. Splitting the spectrum into low / mid / high and mapping
// those to mouth HEIGHT vs WIDTH produces genuinely different vowel shapes from
// nothing but an FFT, with no phoneme table and no per-language work — which
// matters when the language is Thai and viseme data is scarce.

import * as THREE from 'three';

// Around the head, and neck to crown. The columns are NOT evenly spaced in
// angle (see COL_A): the face gets most of them and the back of the skull few,
// which buys detail where it is looked at without paying for it everywhere.
const COLS = 64;
const ROWS = 62;

const T0 = -0.34;          // bottom of the neck, in head-heights below the chin
const T1 = 1.00;           // crown
const HH = 1.95;           // head height, chin to crown, in world units

// Landmarks, in head-heights from the chin. These are the canon: the eye line
// halves the head, the ears span brow to nose base, the mouth sits a third of
// the way from nose base to chin. Getting these wrong is what makes a
// generated face read as "nearly human", and every feature below is placed
// relative to them, so they live here rather than being spelled out inline.
const T_LIP = 0.215, T_EYE = 0.505;
const A_EYE = 0.365;       // angle from the midline to the pupil
const A_EAR = 1.600;       // just behind straight-out-the-side

/** Smooth falloff: 1 at d = 0, 0 at d >= 1. Every feature is built from this. */
function b1(d) {
    if (d >= 1) return 0;
    const t = 1 - d;
    return t * t * (3 - 2 * t);
}
/** Elliptic distance. Math.hypot is correct but slow, and nothing here overflows. */
function el(a, b) { return Math.sqrt(a * a + b * b); }
/** Clamped smoothstep. */
function ss(x) {
    if (x <= 0) return 0;
    if (x >= 1) return 1;
    return x * x * (3 - 2 * x);
}

/**
 * Sample a profile table [[t, value], ...] with a Catmull-Rom spline.
 *
 * Not smoothstep between neighbours: that has zero slope AT every control
 * point, which turns each one into a flat spot and gives the skull a beaded,
 * segmented silhouette. Catmull-Rom passes through the points with a
 * continuous tangent, so the profile reads as one curve.
 */
function cr(tab, t) {
    const n = tab.length;
    if (t <= tab[0][0]) return tab[0][1];
    if (t >= tab[n - 1][0]) return tab[n - 1][1];
    let i = 0;
    while (i < n - 2 && t > tab[i + 1][0]) i++;
    const s = (t - tab[i][0]) / (tab[i + 1][0] - tab[i][0]);
    const p0 = tab[Math.max(0, i - 1)][1], p1 = tab[i][1];
    const p2 = tab[i + 1][1], p3 = tab[Math.min(n - 1, i + 2)][1];
    const s2 = s * s, s3 = s2 * s;
    return 0.5 * (2 * p1 + (p2 - p0) * s +
                  (2 * p0 - 5 * p1 + 4 * p2 - p3) * s2 +
                  (3 * p1 - 3 * p2 + p3 - p0) * s3);
}

// Half-width of each slice. Below t ~ 0.14 this is the NECK, not the jaw: the
// mandible is added as a feature on the front of it (see `sculpt`).
const RX = [[-0.34, 0.206], [-0.18, 0.187], [-0.02, 0.180], [0.14, 0.203],
            [0.28, 0.256], [0.40, 0.294], [0.50, 0.320], [0.62, 0.336],
            [0.74, 0.326], [0.84, 0.292], [0.92, 0.242], [0.97, 0.150],
            [1.00, 0.000]];
// Depth in front of the ear axis. The dip at the eye line is the reason the
// brow reads as a brow: the socket sits behind both the brow and the cheek.
const ZF = [[-0.34, 0.120], [-0.18, 0.132], [-0.02, 0.152], [0.14, 0.208],
            [0.28, 0.290], [0.40, 0.306], [0.505, 0.292], [0.60, 0.322],
            [0.72, 0.318], [0.84, 0.284], [0.92, 0.234], [0.97, 0.142],
            [1.00, 0.000]];
// Depth behind it. Deeper than the front — the occiput is why a human head in
// profile is longer than it is wide.
const ZB = [[-0.34, 0.190], [-0.18, 0.176], [-0.02, 0.172], [0.14, 0.212],
            [0.28, 0.300], [0.40, 0.372], [0.50, 0.408], [0.62, 0.437],
            [0.74, 0.428], [0.84, 0.386], [0.92, 0.312], [0.97, 0.186],
            [1.00, 0.000]];
// Superellipse power. 2 is an ellipse; a skull is boxier than that through the
// temples and rounder at the chin and crown, and this single number is most of
// the difference between "head" and "egg".
const SE = [[-0.34, 2.05], [0.00, 2.15], [0.28, 2.30], [0.50, 2.45],
            [0.66, 2.50], [0.86, 2.32], [1.00, 2.05]];

/**
 * Row r to height t. NOT uniform: the lips, the nostrils and the lid creases
 * have features 0.02 to 0.05 of a head-height tall, and at even spacing the
 * grid stepped straight over them — the profile came out visibly stepped
 * through the whole lower face. This slows the rows down through the middle of
 * the range (the nose and mouth) and speeds them up over the neck and the
 * cranium, where nothing is finer than the grid anyway.
 */
function rowT(r) {
    const f = r / (ROWS - 1);
    const g = f + 0.55 * Math.sin(2 * Math.PI * f) / (2 * Math.PI);
    return T0 + (T1 - T0) * g;
}

/** Everything about one horizontal slice. Hoisted out of the column loop. */
function rowOf(t) {
    return { t, rx: cr(RX, t), zf: cr(ZF, t), zb: cr(ZB, t), e: 2 / cr(SE, t) };
}

// Column angles: 0 is dead ahead, +/-PI the back of the skull. The power
// bunches columns toward the face — with even spacing the back of the head got
// the same wire density as the eyes, which is backwards.
const COL_A = new Float64Array(COLS);
for (let c = 0; c < COLS; c++) {
    const s = (c / COLS) * 2 - 1;
    COL_A[c] = Math.PI * Math.sign(s) * Math.pow(Math.abs(s), 1.32);
}

/**
 * Radial displacement of the surface at (a, t) — the sculpt. Positive pushes
 * out along the slice's outward direction, negative cuts in.
 *
 * `M` carries the live mouth: open 0..1, wide 0..1, smile -1..1. Everything
 * else is static and gets baked once.
 */
function sculpt(a, t, P, M) {
    const A = Math.abs(a);
    let d = 0;

    // ---- mandible ------------------------------------------------------
    // The chin and the jaw hang off the front of the neck column, and both are
    // BROAD, SOFT masses. An earlier pass gave the jawline a sharp lower
    // falloff to "define" it, and a hard edge next to a big chin bump is not an
    // edge, it is a ledge — the cheek folded over it and the whole lower face
    // came out creased.
    d += P.chin * b1(el(A / 0.46, (t - 0.072) / 0.180));
    // `tj` is the height of the jaw's lower border at this angle — it climbs as
    // it runs back from the chin to the corner below the ear.
    const kj = ss(A / 1.45);
    const tj = 0.055 + 0.210 * Math.pow(kj, 1.25);
    const dtj = t - tj;
    d += (0.088 - 0.016 * kj) * P.jaw *
         b1(Math.abs(dtj) / (dtj > 0 ? 0.200 : 0.140)) * ss((2.05 - A) / 0.55);
    // Under the jaw the surface tucks back in toward the throat.
    d -= 0.024 * b1(el((t - (tj - 0.125)) / 0.115, (A - 0.45) / 1.05));
    // Gonial angle — the corner of the jaw. Square on a man, soft on a woman.
    d += P.gonial * b1(el((A - 1.28) / 0.44, (t - 0.255) / 0.115));
    // The crease under the lower lip. Without it the chin has no top edge and
    // the whole lower face reads as one lump.
    d -= 0.022 * b1(el(A / 0.34, (t - 0.125) / 0.046));

    // ---- cheeks --------------------------------------------------------
    // Zygomatic arch: a ridge sweeping from under the outer eye to the ear.
    d += P.cheekBone * b1(el((A - 0.80) / 0.40, (t - 0.478) / 0.062));
    // The hollow under it.
    d -= P.cheekHollow * b1(el((A - 0.66) / 0.34, (t - 0.330) / 0.080));
    // Nasolabial fold, from the nose wing down past the mouth corner.
    d -= 0.010 * b1(el((A - 0.38) / 0.11, (t - 0.280) / 0.080));

    // ---- brow and temple -----------------------------------------------
    d += P.brow * b1(el((t - 0.598) / 0.058, (A - 0.28) / 0.36));
    // Glabella: the small flat between the brows.
    d -= 0.008 * b1(el(A / 0.095, (t - 0.588) / 0.048));
    // Temporal fossa. A skull is hollow at the temple; one that is convex all
    // the way round from brow to ear reads as inflated.
    d -= 0.021 * b1(el((A - 0.80) / 0.38, (t - 0.645) / 0.135));
    // Parietal eminence, the widest part of the cranium.
    d += 0.012 * b1(el((A - 1.15) / 0.50, (t - 0.800) / 0.110));
    // Nuchal flattening at the back of the skull base.
    d -= 0.022 * b1(el((Math.PI - A) / 0.85, (t - 0.270) / 0.110));

    // ---- eyes ----------------------------------------------------------
    // Three layers, and all three are needed: the orbit sets back, the globe
    // and lids bulge inside it, and the crease cuts the lid off from the brow.
    d -= 0.050 * b1(el((A - A_EYE) / 0.235, (t - T_EYE - 0.005) / 0.082));
    d += 0.036 * b1(el((A - A_EYE) / 0.155, (t - T_EYE) / 0.052));
    d -= 0.012 * b1(el((A - A_EYE) / 0.170, (t - 0.556) / 0.022));
    // Inner corner, where the socket is deepest.
    d -= 0.014 * b1(el((A - 0.150) / 0.090, (t - 0.500) / 0.050));

    // ---- nose ----------------------------------------------------------
    // Nasion: a shallow dip at the root, between the brows. Narrow and deep it
    // cuts a crease ACROSS the bridge instead of starting it.
    d -= 0.036 * b1(el(A / 0.200, (t - 0.572) / 0.058));
    // Dorsum: a ridge that widens and rises as it runs down to the tip. The
    // width is the whole game — the first pass made it 0.08 rad wide, and a
    // nose that narrow is a fin, clearly wrong the moment the head turns.
    const nT = ss((0.585 - t) / 0.250);
    d += (0.014 + P.nose * Math.pow(nT, 1.35)) * b1(A / (0.120 + 0.090 * nT)) *
         ss((t - 0.312) / 0.045);
    // Tip, wings, and the undercut that turns the wings into nostrils. That
    // undercut is the whole reason this is a closed surface and not a mask.
    d += 0.034 * b1(el(A / 0.150, (t - 0.368) / 0.050));
    d += 0.024 * b1(el((A - 0.150) / 0.095, (t - 0.348) / 0.040));
    d -= 0.028 * b1(el((A - 0.105) / 0.080, (t - 0.318) / 0.021));
    d -= 0.012 * b1(el(A / 0.100, (t - 0.316) / 0.019));

    // ---- philtrum ------------------------------------------------------
    d -= 0.010 * b1(el(A / 0.048, (t - 0.292) / 0.042));
    d += 0.008 * b1(el((A - 0.064) / 0.036, (t - 0.292) / 0.042));

    // ---- mouth ---------------------------------------------------------
    // Corners ride up on a smile. Doing it here rather than as a separate
    // "smile" pose is what lets a smile survive being spoken through.
    const lift = M.smile * 0.030;
    // Wide spreads the lips and flattens them; the pucker is what is left of
    // `open` when the sound has no high end, which is an "oo".
    const MA = 0.495 * (0.86 + 0.30 * M.wide) * P.mouth;
    const pucker = (1 - M.wide) * M.open;
    const k = Math.min(1, A / MA);                    // 0 midline, 1 corner
    const tl = T_LIP + lift * k * k;                  // corner lift, not a shift

    // The lips part around the seam: the upper rolls up, the lower is carried
    // down by the jaw, and the gap between them recedes into shadow.
    const up = 0.021 + M.open * 0.026;
    const lo = 0.020 + M.open * 0.012;
    // Upper vermilion, with a cupid's bow — the dip at the midline plus two
    // peaks either side of it.
    d += (0.031 + pucker * 0.026) *
         b1(el(A / MA, (t - (tl + up)) / 0.024)) * (1 - 0.32 * b1(A / 0.058));
    d += 0.010 * b1(el((A - 0.100) / 0.058, (t - (tl + up + 0.003)) / 0.018));
    // Lower vermilion: one fuller lobe.
    d += (0.025 + pucker * 0.030) * P.lip *
         b1(el(A / (MA * 0.92), (t - (tl - lo)) / 0.028));
    // The seam itself, and the aperture when she opens her mouth.
    d -= 0.015 * b1(el(A / (MA * 1.06), (t - tl) / 0.013));
    d -= (0.005 + M.open * 0.105) *
         b1(el(A / (MA * 0.94), (t - tl) / (0.016 + M.open * 0.052)));
    // Corners sit back in the face, which is what stops a wide mouth from
    // looking painted on.
    d -= 0.016 * b1(el((A - MA * 0.98) / 0.080, (t - tl) / 0.034));

    // ---- ears ----------------------------------------------------------
    // Radial displacement at A = PI/2 is straight out the side, so an ear is
    // the same arithmetic as a brow: no special case, no extra geometry.
    d += 0.048 * b1(el((A - A_EAR) / 0.290, (t - 0.450) / 0.128));
    d -= 0.026 * b1(el((A - A_EAR) / 0.150, (t - 0.462) / 0.070));   // concha
    d += 0.014 * b1(el((A - 1.575) / 0.155, (t - 0.332) / 0.054));   // lobe

    // ---- neck ----------------------------------------------------------
    // Sternocleidomastoid: the cord from behind the ear to the collarbone.
    d += 0.015 * b1(el((A - 0.95) / 0.32, (t + 0.115) / 0.185));
    d += P.throat * b1(el(A / 0.17, (t + 0.070) / 0.055));

    return d;
}

/**
 * One vertex. `R` is the precomputed slice, `M` the live mouth.
 *
 * Returns [x, y, z, nx, nz] — the last two are the outward direction in the
 * horizontal plane, kept because the lids and the eyebrows have to move along
 * it and recomputing it there is pure waste.
 */
function surface(a, R, P, M) {
    const sa = Math.sin(a), ca = Math.cos(a);
    // Superellipse. The sign has to be carried around the pow, or the left half
    // of the head folds onto the right.
    const sx = Math.sign(sa) * Math.pow(Math.abs(sa), R.e);
    const sz = Math.sign(ca) * Math.pow(Math.abs(ca), R.e);
    const L = Math.sqrt(sx * sx + sz * sz) || 1;
    const nx = sx / L, nz = sz / L;

    // Front and back depth blended by facing, so one slice can be flat in front
    // and bulging behind without a crease at the sides.
    const rz = R.zb + (R.zf - R.zb) * (0.5 + 0.5 * ca);
    const d = sculpt(a, R.t, P, M);

    return [(R.rx * sx + d * nx) * P.wide * HH,
            R.t * HH,
            (rz * sz + d * nz) * HH,
            nx, nz];
}

/** Radial-gradient sprite used for the irises. */
function glowTexture() {
    const c = document.createElement('canvas');
    c.width = c.height = 128;
    const g = c.getContext('2d');
    const grd = g.createRadialGradient(64, 64, 0, 64, 64, 64);
    grd.addColorStop(0.00, 'rgba(225,255,238,1)');
    grd.addColorStop(0.30, 'rgba(90,255,165,0.92)');
    grd.addColorStop(0.52, 'rgba(30,212,124,0.46)');
    grd.addColorStop(0.78, 'rgba(10,150,80,0.10)');
    grd.addColorStop(1.00, 'rgba(0,90,55,0)');
    g.fillStyle = grd;
    g.fillRect(0, 0, 128, 128);
    return new THREE.CanvasTexture(c);
}

// Alpha from three things: how much a wire faces you, how far away it is, and
// how close it is to the silhouette. `aFade` is a per-vertex multiplier, used
// to dissolve the bottom of the neck instead of cutting it off square.
const HOLO_VS = `
attribute float aFade;
varying float vFacing;
varying float vDepth;
varying float vFade;
void main() {
    vec4 mv = modelViewMatrix * vec4(position, 1.0);
    vFacing = dot(normalize(normalMatrix * normal), normalize(-mv.xyz));
    vDepth  = -mv.z;
    vFade   = aFade;
    gl_Position = projectionMatrix * mv;
}`;

const HOLO_FS = `
uniform vec3  uColor;
uniform float uNear;
uniform float uFar;
uniform float uGain;
varying float vFacing;
varying float vDepth;
varying float vFade;
void main() {
    float facing = smoothstep(-0.55, 0.45, vFacing);
    float depth  = 1.0 - smoothstep(uNear, uFar, vDepth);
    float rim    = pow(1.0 - abs(vFacing), 5.0);
    float a = (0.040 + 0.55 * facing) * (0.28 + 0.72 * depth) + 0.34 * rim * depth;
    a *= vFade * uGain;
    if (a <= 0.002) discard;
    gl_FragColor = vec4(uColor + rim * 0.30, a);
}`;

function holoMaterial(gain, color = 0x35e08a) {
    return new THREE.ShaderMaterial({
        vertexShader: HOLO_VS,
        fragmentShader: HOLO_FS,
        uniforms: {
            uColor: { value: new THREE.Color(color) },
            uNear: { value: 4.30 },
            uFar: { value: 6.60 },
            uGain: { value: gain },
        },
        transparent: true,
        depthWrite: false,
        // No depth test at all. The facing term already does the depth cueing,
        // so testing only produces sorting artefacts between the head, the eyes
        // and the brows for no visual gain.
        depthTest: false,
        blending: THREE.AdditiveBlending,
    });
}

/**
 * How much of the mandible a point is on. The upper boundary is the lip line at
 * the front and the ear at the side, which is where the real one runs; below
 * the chin it fades, so the throat stretches instead of tearing away from it.
 * Shared, because the lip LINES have to swing with the jaw the mesh swings.
 */
function jawWeight(A, t) {
    return ss((T_LIP + 0.245 * ss((A - 0.55) / 0.75) - t) / 0.20) *
           ss((t + 0.14) / 0.17) * ss((2.05 - A) / 0.70);
}

// The condyle: the mandible swings about a point just in front of the ear.
const HINGE_Y = 0.500 * HH, HINGE_Z = -0.06 * HH;

/**
 * Landmark lines drawn on the surface: the eyebrows, the opening of each eye
 * with its lid crease, and the three borders of the mouth.
 *
 * The wireframe carries the FORM; these carry the FEATURES, and the face needs
 * both. An even grid of wires gives the eye no edges to find a face in, so the
 * sculpt alone read as a mannequin no matter how good the profile was — and
 * the obvious fix, a wireframe sphere for each eyeball, was worse: with the
 * depth test off (which the hologram look requires) the spheres showed straight
 * through the skull, so in profile she had two balls floating in her temple.
 * An almond of lid margins lying ON the surface has no such problem, and reads
 * as an eye from every angle.
 */
const DETAIL = [
    { n: 15, closed: false },   // brow, left / right
    { n: 15, closed: false },
    { n: 22, closed: true },    // eye opening
    { n: 22, closed: true },
    { n: 11, closed: false },   // lid crease
    { n: 11, closed: false },
    { n: 21, closed: false },   // upper lip border
    { n: 21, closed: false },   // lower lip border
    { n: 21, closed: false },   // the seam between them
    { n: 9, closed: false },    // crease round the wing of the nose
    { n: 9, closed: false },
];
const DETAIL_N = DETAIL.reduce((a, g) => a + g.n, 0);

function buildHead(female) {
    const g = new THREE.Group();

    // Proportions carry the gender. These are scalars on one sculpt, not a
    // second set of formulae — a woman's skull is the same skull with a lighter
    // brow, a rounder jaw, fuller lips and more cheekbone.
    const P = female
        ? { wide: 0.985, brow: 0.032, chin: 0.048, jaw: 0.92, gonial: 0.014,
            nose: 0.062, lip: 1.12, mouth: 0.96, throat: 0.004,
            cheekBone: 0.024, cheekHollow: 0.013 }
        : { wide: 1.055, brow: 0.050, chin: 0.066, jaw: 1.08, gonial: 0.030,
            nose: 0.074, lip: 0.94, mouth: 1.04, throat: 0.011,
            cheekBone: 0.019, cheekHollow: 0.008 };

    const rows = new Array(ROWS);
    for (let r = 0; r < ROWS; r++) rows[r] = rowOf(rowT(r));

    const N = COLS * ROWS;
    const pos = new Float32Array(N * 3);
    const base = new Float32Array(N * 3);
    const nrm = new Float32Array(N * 3);
    const fade = new Float32Array(N);
    const dir = new Float32Array(N * 2);
    const jawW = new Float32Array(N);
    const lidW = new Float32Array(N);

    const at = (c, r) => r * COLS + (c % COLS);
    const idx = [];
    for (let r = 0; r < ROWS - 1; r++)          // the crown row is a single
        for (let c = 0; c < COLS; c++) {        // point; ringing it draws a knot
            idx.push(at(c, r), at(c + 1, r));
            idx.push(at(c, r), at(c, r + 1));
        }

    const M0 = { open: 0, wide: 0, smile: 0 };
    const mouthIdx = [], dynSet = new Set();

    for (let r = 0; r < ROWS; r++) {
        const R = rows[r], t = R.t;
        const f = ss((t - T0) / 0.30);           // the neck dissolves, not ends
        for (let c = 0; c < COLS; c++) {
            const i = at(c, r), a = COL_A[c], A = Math.abs(a);
            const p = surface(a, R, P, M0);
            base[i * 3] = p[0]; base[i * 3 + 1] = p[1]; base[i * 3 + 2] = p[2];
            dir[i * 2] = p[3]; dir[i * 2 + 1] = p[4];
            fade[i] = f;

            jawW[i] = jawWeight(A, t);
            lidW[i] = b1(el((A - A_EYE) / 0.235, (t - 0.545) / 0.062));

            // Vertices the mouth sculpt can actually reach. Re-evaluating only
            // these each frame keeps the per-frame cost at a couple of hundred
            // vertices instead of three thousand.
            if (A < 0.85 && t > T_LIP - 0.17 && t < T_LIP + 0.17) {
                mouthIdx.push(i); dynSet.add(i);
            }
            if (jawW[i] > 0.001 || lidW[i] > 0.001) dynSet.add(i);
        }
    }
    pos.set(base);

    // Normals from the grid itself. Analytic normals through twenty-odd feature
    // bumps would be a second sculpt to keep in sync; central differences over
    // the neighbours are exact enough for a lighting term and cannot drift out
    // of agreement with the surface.
    for (let r = 0; r < ROWS; r++)
        for (let c = 0; c < COLS; c++) {
            const i = at(c, r);
            const lf = at(c + COLS - 1, r) * 3, rt = at(c + 1, r) * 3;
            const dn = at(c, Math.max(0, r - 1)) * 3;
            const up = at(c, Math.min(ROWS - 1, r + 1)) * 3;
            const ux = base[rt] - base[lf], uy = base[rt + 1] - base[lf + 1],
                  uz = base[rt + 2] - base[lf + 2];
            const vx = base[up] - base[dn], vy = base[up + 1] - base[dn + 1],
                  vz = base[up + 2] - base[dn + 2];
            let nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            const L = Math.sqrt(nx * nx + ny * ny + nz * nz);
            if (L > 1e-9) { nx /= L; ny /= L; nz /= L; }
            else { nx = dir[i * 2]; ny = 0; nz = dir[i * 2 + 1]; }
            // At the crown the cross product is degenerate and can flip; the
            // radial direction is the tie-breaker.
            if (nx * dir[i * 2] + nz * dir[i * 2 + 1] < 0) { nx = -nx; ny = -ny; nz = -nz; }
            nrm[i * 3] = nx; nrm[i * 3 + 1] = ny; nrm[i * 3 + 2] = nz;
        }

    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setAttribute('normal', new THREE.BufferAttribute(nrm, 3));
    geo.setAttribute('aFade', new THREE.BufferAttribute(fade, 1));
    geo.setIndex(idx);
    const mesh = new THREE.LineSegments(geo, holoMaterial(1.0));
    mesh.frustumCulled = false;
    g.add(mesh);

    // ---- irises --------------------------------------------------------
    // All that is left of the eyeball: a glow inside the lid opening. It is a
    // sprite because it is light, not surface — it has no silhouette to get
    // wrong, and it can sit slightly proud of the face without looking pasted
    // on the way an opaque globe did.
    const tex = glowTexture();
    const eyes = [];
    for (let i = 0; i < 2; i++) {
        const iris = new THREE.Sprite(new THREE.SpriteMaterial({
            map: tex, color: 0x62ffae, transparent: true,
            blending: THREE.AdditiveBlending, depthWrite: false, depthTest: false,
            opacity: 0.95,
        }));
        iris.scale.set(0.108, 0.088, 1);
        g.add(iris);
        eyes.push(iris);
    }

    // ---- landmark lines -------------------------------------------------
    const dp = new Float32Array(DETAIL_N * 3);
    const dnr = new Float32Array(DETAIL_N * 3);
    const df = new Float32Array(DETAIL_N).fill(1);
    const di = [];
    let off = 0;
    for (const grp of DETAIL) {
        for (let k = 0; k < grp.n - 1; k++) di.push(off + k, off + k + 1);
        if (grp.closed) di.push(off + grp.n - 1, off);
        off += grp.n;
    }
    const dgeo = new THREE.BufferGeometry();
    dgeo.setAttribute('position', new THREE.BufferAttribute(dp, 3));
    dgeo.setAttribute('normal', new THREE.BufferAttribute(dnr, 3));
    dgeo.setAttribute('aFade', new THREE.BufferAttribute(df, 1));
    dgeo.setIndex(di);
    const detail = new THREE.LineSegments(dgeo, holoMaterial(2.0));
    detail.frustumCulled = false;
    g.add(detail);

    return {
        group: g, mesh, geo, pos, base, dir, jawW, lidW, rows, P, eyes,
        mouthIdx: Int32Array.from(mouthIdx), dyn: Int32Array.from(dynSet),
        detail, dgeo, dp, dnr, tex, mats: [mesh.material, detail.material],
    };
}

/**
 * Emotions as four continuous dials rather than a set of poses, so they blend:
 * a face can be 60% pleased and still opening its mouth to speak, which a
 * swap-the-pose approach cannot do.
 *
 *   smile     -1 frown .. +1 smile
 *   browTilt  -1 stern .. +1 worried
 *   browRaise -1 lowered .. +1 raised
 *   eyeOpen    0 squint .. 1.4 wide
 */
export const MOODS = {
    neutral:   { smile:  0.00, browTilt:  0.00, browRaise:  0.00, eyeOpen: 1.00 },
    // A real smile narrows the eyes. Without that it reads as a grimace.
    happy:     { smile:  0.75, browTilt:  0.10, browRaise:  0.15, eyeOpen: 0.82 },
    pleased:   { smile:  0.42, browTilt:  0.05, browRaise:  0.08, eyeOpen: 0.92 },
    concerned: { smile: -0.42, browTilt:  0.65, browRaise:  0.10, eyeOpen: 1.06 },
    alert:     { smile: -0.05, browTilt: -0.15, browRaise:  0.80, eyeOpen: 1.30 },
    thinking:  { smile: -0.10, browTilt: -0.25, browRaise: -0.12, eyeOpen: 0.78 },
    sorry:     { smile: -0.55, browTilt:  0.85, browRaise: -0.05, eyeOpen: 0.88 },
};

const LOW = [0, 8], MID = [8, 40], HIGH = [40, 120];

function band(data, [a, b]) {
    let s = 0;
    for (let i = a; i < b && i < data.length; i++) s += data[i];
    return s / Math.max(1, Math.min(b, data.length) - a) / 255;
}

/**
 * The sound behind her — overlapping sine curves whose height follows the
 * loudness of what she is saying.
 *
 * A 2D canvas rather than more three.js geometry: this is a flat backdrop that
 * never rotates with the head, and rebuilding a line mesh every frame to draw
 * four curves would cost more than the whole face does.
 *
 * It stays faint on purpose. The face is what you look at; this is the room it
 * sits in, and a backdrop that competes for attention with the thing in front
 * of it is just noise. It also never stops moving — a visualiser that goes
 * flat between sentences reads as broken rather than as quiet.
 */
class WaveField {
    constructor(host) {
        this.host = host;
        const c = document.createElement('canvas');
        Object.assign(c.style, {
            position: 'absolute', left: '0', top: '0',
            width: '100%', height: '100%',
            pointerEvents: 'none',          // never steal a click from the face
        });
        // First child, so it sits under the WebGL canvas without either of
        // them needing a z-index.
        host.insertBefore(c, host.firstChild);
        this.canvas = c;
        this.g = c.getContext('2d');
        this.phase = 0;
        this.level = 0;
        this.fit();
    }

    fit() {
        const w = this.host.clientWidth || 260, h = this.host.clientHeight || 300;
        const d = Math.min(2, window.devicePixelRatio || 1);
        this.w = w; this.h = h;
        this.canvas.width = Math.round(w * d);
        this.canvas.height = Math.round(h * d);
        this.g.setTransform(d, 0, 0, d, 0, 0);
    }

    /** @param {number} level 0..1 loudness right now. */
    draw(level) {
        // Rises with the voice, falls slowly. Falling at the attack rate makes
        // the curves flick between syllables instead of riding the sentence.
        this.level += (level - this.level) * (level > this.level ? 0.5 : 0.06);
        this.phase += 0.016 + this.level * 0.05;

        const g = this.g, w = this.w, h = this.h, cy = h * 0.62;
        g.clearRect(0, 0, w, h);
        g.globalCompositeOperation = 'lighter';   // overlaps brighten, like light

        // Idle amplitude has to be big enough to LOOK like a wave. Set small
        // "so it stays subtle", four curves of different frequency all flatten
        // onto the same pixel row and add up under `lighter` into one bright
        // horizontal line drawn straight across her eyes — subtlety belongs in
        // the alpha, not in the amplitude.
        const amp = 16 + Math.sin(this.phase * 0.7) * 5 + this.level * (h * 0.24);
        const LINES = [
            { k: 1.0, spd: 1.00, off: 0.0, a: 0.17, lw: 1.4 },
            { k: 1.6, spd: -0.70, off: 1.1, a: 0.12, lw: 1.1 },
            { k: 2.4, spd: 1.40, off: 2.3, a: 0.08, lw: 1.0 },
            { k: 3.6, spd: -1.85, off: 3.7, a: 0.05, lw: 0.9 },
        ];

        // Fade the ink out at both ends as well as flattening the curve.
        // Amplitude alone is not enough: where the envelope reaches zero all
        // four curves land on the same row and add up under `lighter` into a
        // hard horizontal line running off both edges — the exact artifact the
        // envelope was supposed to prevent.
        const fade = g.createLinearGradient(0, 0, w, 0);
        fade.addColorStop(0.00, 'rgba(53,224,138,0)');
        fade.addColorStop(0.22, 'rgba(53,224,138,1)');
        fade.addColorStop(0.78, 'rgba(53,224,138,1)');
        fade.addColorStop(1.00, 'rgba(53,224,138,0)');

        const step = Math.max(2, Math.round(w / 150));
        for (const L of LINES) {
            g.beginPath();
            for (let x = 0; x <= w; x += step) {
                const u = x / w;
                // Taper to nothing at both edges so the curves read as a band
                // of sound rather than as lines running off the sides.
                const env = Math.pow(Math.sin(u * Math.PI), 2.1);
                // `off` staggers the curves so they do not all cross zero at
                // the same x — without it they pinch into one line four times
                // across the width and the stack is visible as a defect.
                const y = cy + Math.sin(u * Math.PI * 2 * L.k + this.phase * L.spd + L.off)
                             * amp * env * (1 - L.k * 0.08);
                x === 0 ? g.moveTo(x, y) : g.lineTo(x, y);
            }
            g.strokeStyle = fade;
            g.globalAlpha = L.a;      // per-curve weight; the gradient carries the shape
            g.lineWidth = L.lw;
            g.stroke();
            g.globalAlpha = 1;
        }

        // One soft pulse behind the curves, so loud passages glow rather than
        // only growing taller.
        if (this.level > 0.02) {
            const r = g.createRadialGradient(w / 2, cy, 0, w / 2, cy, Math.max(w, h) * 0.42);
            r.addColorStop(0, `rgba(53,224,138,${0.055 * this.level})`);
            r.addColorStop(1, 'rgba(53,224,138,0)');
            g.fillStyle = r;
            g.fillRect(0, 0, w, h);
        }
        g.globalCompositeOperation = 'source-over';
    }

    dispose() { this.canvas.remove(); }
}

export class AssistantFace {
    constructor(host, female = true) {
        this.host = host;
        this.female = female;
        this.speaking = false;
        this._open = 0;
        this._wide = 0;
        this._lid = 0;
        this._blinkAt = 0;
        this._gazeAt = 0;
        this._gaze = { x: 0, y: 0, tx: 0, ty: 0 };
        this.mood = { ...MOODS.neutral };
        this._mood = { ...MOODS.neutral };

        const w = host.clientWidth || 260, h = host.clientHeight || 320;
        this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
        this.renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
        this.renderer.setSize(w, h, false);
        host.appendChild(this.renderer.domElement);
        // After the renderer, so insertBefore(firstChild) puts it underneath.
        this.wave = new WaveField(host);

        this.scene = new THREE.Scene();
        this.camera = new THREE.PerspectiveCamera(32, w / h, 0.1, 100);
        this.camera.position.set(0, 0, 4.45);

        this.head = buildHead(female);
        // The head is measured from the chin, so it has to be dropped to sit in
        // frame; the neck then runs off the bottom edge as it fades.
        this.head.group.position.y = -HH * 0.44;
        this.scene.add(this.head.group);
        this._pose(0, 0, 0, 0);
        this._detail(0, 0, 0, 0);

        this._loop = this._loop.bind(this);
        this._raf = requestAnimationFrame(this._loop);
    }

    /** @param {string|object} m a MOODS key, or partial dials to blend toward. */
    setMood(m) {
        const target = typeof m === 'string' ? (MOODS[m] ?? MOODS.neutral) : m;
        this.mood = { ...this.mood, ...target };
        return this;
    }

    setFemale(female) {
        if (female === this.female) return;
        this.female = female;
        this.scene.remove(this.head.group);
        this._disposeHead();
        this.head = buildHead(female);
        this.head.group.position.y = -HH * 0.44;
        this.scene.add(this.head.group);
        this._pose(this._open, this._wide, this._mood.smile, this._lid);
        this._detail(this._open, this._wide, this._mood.smile, this._lid);
    }

    _disposeHead() {
        const H = this.head;
        H.geo.dispose(); H.dgeo.dispose(); H.tex.dispose();
        for (const m of H.mats) m.dispose();
        for (const e of H.eyes) e.material.dispose();
    }

    /**
     * Put the face in a pose. Only the vertices that can move are touched: the
     * mouth is re-sculpted properly (a couple of hundred), the mandible is
     * rotated off the cached rest pose (about nine hundred), and the lids slide
     * down over the globes. Re-running the whole sculpt at 60fps would be three
     * thousand vertices of trigonometry to move a lip.
     */
    _pose(open, wide, smile, lid) {
        const H = this.head, P = H.P, base = H.base, pos = H.pos, D = H.dyn;

        for (let k = 0; k < D.length; k++) {
            const j = D[k] * 3;
            pos[j] = base[j]; pos[j + 1] = base[j + 1]; pos[j + 2] = base[j + 2];
        }

        const M = { open, wide, smile };
        for (let k = 0; k < H.mouthIdx.length; k++) {
            const i = H.mouthIdx[k], j = i * 3;
            const p = surface(COL_A[i % COLS], H.rows[(i / COLS) | 0], P, M);
            pos[j] = p[0]; pos[j + 1] = p[1]; pos[j + 2] = p[2];
        }

        // The mandible swings about the condyle, just in front of the ear. A
        // mouth that opens as a hole in a static face is the thing that reads
        // as a puppet; the jaw coming down with it is what reads as speech.
        const ang = open * 0.20;
        if (ang > 1e-4) {
            for (let k = 0; k < D.length; k++) {
                const i = D[k], w = H.jawW[i];
                if (w < 0.001) continue;
                const j = i * 3, phi = ang * w;
                // Small-angle: under 0.2 rad the error is well below a pixel,
                // and this runs on every mandible vertex every frame.
                const cs = 1 - phi * phi * 0.5, sn = phi - phi * phi * phi / 6;
                const dy = pos[j + 1] - HINGE_Y, dz = pos[j + 2] - HINGE_Z;
                pos[j + 1] = HINGE_Y + dy * cs - dz * sn;
                pos[j + 2] = HINGE_Z + dy * sn + dz * cs;
            }
        }

        // Lids: the upper lid sweeps down the front of the globe. Blinking by
        // fading the iris alone left the eye socket wide open, which reads as a
        // light going out rather than as an eyelid.
        if (lid > 0.002) {
            for (let k = 0; k < D.length; k++) {
                const i = D[k], w = H.lidW[i];
                if (w < 0.001) continue;
                const j = i * 3, s = lid * w;
                pos[j + 1] -= s * 0.086 * HH;
                pos[j] += s * 0.020 * HH * H.dir[i * 2];
                pos[j + 2] += s * 0.020 * HH * H.dir[i * 2 + 1];
            }
        }

        H.geo.attributes.position.needsUpdate = true;
    }

    /**
     * Redraw the landmark lines and place the irises. About 150 points, all of
     * them sampled from the same `surface` the mesh uses, so a line can never
     * drift off the form it is supposed to be describing.
     */
    _detail(open, wide, smile, lid) {
        const H = this.head, P = H.P, M = this._mood;
        const Mo = { open, wide, smile };
        const dp = H.dp, dn = H.dnr;
        let o = 0;

        // Anything a line lands on gets pushed this far clear of the mesh, or
        // it z-fights the wires it is drawn over.
        const LIFT = 0.017 * HH;
        const put = (a, t) => {
            const p = surface(a, rowOf(t), P, Mo);
            let y = p[1], z = p[2] + p[4] * LIFT;
            // The lower lip belongs to the mandible, so it has to swing with
            // it: a mouth line that stays put while the jaw drops opens a gap
            // between the drawing and the form.
            const phi = open * 0.20 * jawWeight(Math.abs(a), t);
            if (phi > 1e-4) {
                const cs = Math.cos(phi), sn = Math.sin(phi);
                const dy = y - HINGE_Y, dz = z - HINGE_Z;
                y = HINGE_Y + dy * cs - dz * sn;
                z = HINGE_Z + dy * sn + dz * cs;
            }
            dp[o] = p[0] + p[3] * LIFT; dp[o + 1] = y; dp[o + 2] = z;
            dn[o] = p[3]; dn[o + 1] = 0; dn[o + 2] = p[4];
            o += 3;
        };

        // Eyebrows. A natural brow arches over the middle of the eye and drops
        // at the tail; the mood tilts it about that shape rather than replacing
        // it, so "worried" still looks like the same brow.
        for (const sgn of [-1, 1])
            for (let k = 0; k < 15; k++) {
                const f = k / 14;
                put(sgn * (0.135 + 0.475 * f),
                    0.606 + 0.022 * Math.sin(Math.PI * f * 0.9)
                          + M.browRaise * 0.038
                          + M.browTilt * (0.034 * (1 - f) - 0.016 * f)
                          + Math.max(0, M.smile) * 0.010);
            }

        // The palpebral opening: an almond, wider than it is tall, canted up at
        // the outer corner. `cs` runs -1 at the inner corner to +1 at the outer.
        const lidOpen = Math.max(0, 1 - lid);
        for (const sgn of [-1, 1])
            for (let k = 0; k < 22; k++) {
                const th = (k / 22) * Math.PI * 2;
                const cs = Math.cos(th), sn = Math.sin(th);
                // The upper lid travels and the lower barely moves, which is
                // what a blink actually looks like.
                const h = sn > 0 ? 0.034 * lidOpen
                                : 0.020 * (1 + Math.max(0, -lid) * 0.6);
                put(sgn * (A_EYE + 0.215 * cs),
                    T_EYE + h * sn + 0.013 * cs - lid * 0.010);
            }

        // The crease above the lid, which drops as the eye closes.
        for (const sgn of [-1, 1])
            for (let k = 0; k < 11; k++) {
                const f = k / 10, cs = -0.75 + 1.65 * f;
                put(sgn * (A_EYE + 0.215 * cs),
                    T_EYE + 0.044 + 0.022 * Math.sin(Math.PI * f) + 0.013 * cs
                          - lid * 0.022);
            }

        // The mouth: the top of the upper lip, the bottom of the lower, and the
        // seam. All three meet at the corners, which is what makes them read as
        // one mouth rather than as three stripes.
        const MA = 0.495 * (0.86 + 0.30 * wide) * P.mouth;
        const up = 0.021 + open * 0.026, lo = 0.020 + open * 0.012;
        for (const band of [1, -1, 0])
            for (let k = 0; k < 21; k++) {
                const u = (k / 20) * 2 - 1, kk = Math.abs(u);
                const taper = 1 - kk * kk;
                const tl = T_LIP + smile * 0.030 * kk * kk;
                put(u * MA, tl + band * (band > 0 ? up + 0.017 : lo + 0.015) * taper);
            }

        // The wing of the nose. From the front the nose is the one feature the
        // wireframe cannot show on its own — it points at the camera, so its
        // whole form is edge-on and the grid runs straight over it. This groove
        // is what gives it an outline.
        for (const sgn of [-1, 1])
            for (let k = 0; k < 9; k++) {
                const th = (115 - 185 * (k / 8)) * Math.PI / 180;
                put(sgn * (0.155 + 0.080 * Math.cos(th)),
                    0.352 + 0.040 * Math.sin(th));
            }

        H.dgeo.attributes.position.needsUpdate = true;
        H.dgeo.attributes.normal.needsUpdate = true;

        // Irises ride inside the opening and follow the gaze.
        for (let i = 0; i < 2; i++) {
            const sgn = i ? 1 : -1;
            const p = surface(sgn * (A_EYE + this._gaze.x * 0.11),
                              rowOf(T_EYE + this._gaze.y * 0.05), P, Mo);
            H.eyes[i].position.set(p[0] + p[3] * LIFT * 1.4, p[1],
                                   p[2] + p[4] * LIFT * 1.4);
            // The lid is a line, not a solid, so it cannot hide the glow: the
            // iris has to fade on its own or a blink leaves two dots behind.
            H.eyes[i].material.opacity = 0.95 * Math.min(1, Math.max(0, 1 - lid * 1.3));
            const sc = 0.86 + M.eyeOpen * 0.16;
            H.eyes[i].scale.set(0.108 * sc, 0.088 * sc, 1);
        }
    }

    /**
     * Play an mp3 and drive the mouth from it. The AudioContext is created on
     * this call, not in the constructor: browsers refuse to start one outside
     * a user gesture, and one created at load time arrives suspended and
     * silently stays that way — audible as "the face never moves", with no
     * error anywhere.
     */
    async speak(url) {
        this.stop();
        const audio = new Audio(url);
        audio.crossOrigin = 'anonymous';
        this.audio = audio;

        const Ctx = window.AudioContext || window.webkitAudioContext;
        this.ctx = this.ctx || new Ctx();
        if (this.ctx.state === 'suspended') { try { await this.ctx.resume(); } catch {} }

        const src = this.ctx.createMediaElementSource(audio);
        const an = this.ctx.createAnalyser();
        // SMALL WINDOW, ALMOST NO SMOOTHING. The analyser's
        // smoothingTimeConstant is an exponential average over frames: at 0.55
        // each frame was 55% history, which is two or three frames of lag on
        // top of the FFT window and enough to blur consecutive syllables into
        // one long vowel. Thai is syllable-timed, so that blur is exactly the
        // thing that made the mouth look out of step with the words.
        an.fftSize = 256;
        an.smoothingTimeConstant = 0.1;
        src.connect(an); an.connect(this.ctx.destination);
        this.analyser = an;
        this.freq = new Uint8Array(an.frequencyBinCount);
        // Time-domain samples drive HOW OPEN the mouth is. An amplitude
        // envelope read straight from the waveform has no analysis lag at all,
        // where the same envelope derived from FFT magnitudes inherits the
        // window and the smoothing. Shape stays on the FFT — vowel colour
        // changes far slower than loudness, so it can afford the latency that
        // the opening cannot.
        this.time = new Uint8Array(an.fftSize);

        this.speaking = true;
        try { await audio.play(); } catch (e) { this.speaking = false; throw e; }
        await new Promise(res => { audio.onended = res; audio.onerror = res; });
        this.speaking = false;
        return true;
    }

    stop() {
        try { this.audio?.pause(); } catch {}
        this.speaking = false;
    }

    _loop(t) {
        this._raf = requestAnimationFrame(this._loop);
        const H = this.head;

        const pSmile = this._mood.smile, pRaise = this._mood.browRaise,
              pTilt = this._mood.browTilt, pAper = this._mood.eyeOpen;
        for (const k of Object.keys(this._mood))
            this._mood[k] += ((this.mood[k] ?? 0) - this._mood[k]) * 0.08;
        const M = this._mood;

        let open = 0, wide = 0, level = 0;
        if (this.speaking && this.analyser) {
            // Opening: RMS of the raw waveform, gated by a noise floor so room
            // tone and codec hiss do not hold the mouth permanently ajar.
            this.analyser.getByteTimeDomainData(this.time);
            let sum = 0;
            for (let i = 0; i < this.time.length; i++) {
                const v = (this.time[i] - 128) / 128;
                sum += v * v;
            }
            const rms = Math.sqrt(sum / this.time.length);
            open = Math.min(1, Math.max(0, (rms - 0.012) * 6.2));
            // The backdrop takes the loudness ungated: the mouth needs a noise
            // floor so hiss does not hold it ajar, but the waves are allowed to
            // shimmer on the quiet parts.
            level = Math.min(1, rms * 3.6);

            // Shape: which way the energy leans. High spreads the lips ("ee"),
            // low with little high rounds them ("oo").
            this.analyser.getByteFrequencyData(this.freq);
            const lo = band(this.freq, LOW), mid = band(this.freq, MID), hi = band(this.freq, HIGH);
            wide = Math.min(1, hi * 2.4 + mid * 0.7 - lo * 0.6);
        }
        // A smile widens the mouth even in silence, so the mood is visible on
        // a closed face.
        wide = Math.max(wide, Math.max(0, M.smile) * 0.5);

        // Asymmetric smoothing: mouths open faster than they close, and equal
        // rates read as mush. Attack near-instant, release merely quick — a
        // mouth that closes as fast as it opens chatters between syllables, one
        // that opens slowly is always a beat behind the word.
        const po = this._open, pw = this._wide, pl = this._lid;
        this._open += (open - this._open) * (open > this._open ? 0.90 : 0.38);
        this._wide += (wide - this._wide) * 0.34;

        // Eyes close on a blink and sit at the aperture the mood asks for. A
        // face that never blinks is unsettling in a way people notice without
        // being able to say why.
        if (t > this._blinkAt) { this._blinkAt = t + 2200 + Math.random() * 3200; this._blinkStart = t; }
        const bp = (t - (this._blinkStart ?? -1e9)) / 130;
        const blink = bp >= 0 && bp <= 1 ? Math.abs(Math.sin(bp * Math.PI)) : 0;
        this._lid = Math.min(1, Math.max(-0.30, blink + (1 - M.eyeOpen) * 0.55));

        // Gaze. Eyes that hold dead centre look painted on; real ones make
        // small jumps and then settle, so this picks a new target every couple
        // of seconds and eases toward it.
        if (t > this._gazeAt) {
            this._gazeAt = t + 1400 + Math.random() * 2600;
            this._gaze.tx = (Math.random() - 0.5) * 0.30;
            this._gaze.ty = (Math.random() - 0.5) * 0.16;
        }
        const pgx = this._gaze.x, pgy = this._gaze.y;
        this._gaze.x += (this._gaze.tx - this._gaze.x) * 0.14;
        this._gaze.y += (this._gaze.ty - this._gaze.y) * 0.14;

        const moved = Math.abs(this._open - po) > 0.0012 ||
                      Math.abs(this._wide - pw) > 0.0012 ||
                      Math.abs(this._lid - pl) > 0.0025 ||
                      Math.abs(M.smile - pSmile) > 0.0015;
        if (moved) this._pose(this._open, this._wide, M.smile, this._lid);
        // The lines are 150 points against the mesh's three thousand, so they
        // redraw on anything that could nudge them — including the gaze, which
        // the mesh does not care about at all.
        if (moved || Math.abs(M.browRaise - pRaise) > 0.0012 ||
            Math.abs(M.browTilt - pTilt) > 0.0012 ||
            Math.abs(M.eyeOpen - pAper) > 0.0012 ||
            Math.abs(this._gaze.x - pgx) > 0.0004 ||
            Math.abs(this._gaze.y - pgy) > 0.0004)
            this._detail(this._open, this._wide, M.smile, this._lid);

        const s = t / 1000;
        H.group.rotation.y = Math.sin(s * 0.4) * 0.15;
        H.group.rotation.x = Math.sin(s * 0.31) * 0.05 + (this.speaking ? 0.03 : 0);
        // The cocked head is half of what makes "thinking" read as thinking
        // rather than as a blank stare.
        H.group.rotation.z = -M.browTilt * 0.05 + (M.eyeOpen < 0.85 ? 0.06 : 0);

        this.wave.draw(level);
        this.renderer.render(this.scene, this.camera);
    }

    resize(w, h) {
        this.camera.aspect = w / h;
        this.camera.updateProjectionMatrix();
        this.renderer.setSize(w, h, false);
        // The face is clamped narrower than the panel; the waves are not, so
        // they measure the host rather than taking w/h.
        this.wave.fit();
    }

    dispose() {
        cancelAnimationFrame(this._raf);
        this.stop();
        try { this.ctx?.close(); } catch {}
        this._disposeHead();
        this.wave.dispose();
        this.renderer.dispose();
        this.renderer.domElement.remove();
    }
}
