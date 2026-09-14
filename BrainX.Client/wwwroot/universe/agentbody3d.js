/* Agent Bus, rendered as a human body — the Neural Brain theme's counterpart
 * to the solar system in agentbus3d.js.
 *
 * The brain is BrainX, in the head where a brain belongs; every agent on the
 * bus is an organ, wired to it by a real nerve route. The mapping follows what
 * each agent DOES:
 *
 *   Claude  the heart     — the one the rest depends on, and already coral
 *   Codex   right hand    — writes the code
 *   CluadeX left hand     — the other writing hand
 *   Gemini  eyes          Grok  ears
 *   Unity / Unreal  the legs — engines, the parts that move the body
 *   anyone else  the next free viscus (lungs, liver, stomach, kidneys, gut,
 *                spleen), then ganglia down the spine
 *   a second Claude (local-agent mode)  a small ganglion beside the heart —
 *                the moon of the old picture, the same two true things: its
 *                own body and traffic, plainly belonging to what it circles
 *
 * Presence is tissue: a live organ glows in its agent's colour and keeps its
 * rhythm (the heart beats, the lungs breathe), a quiet one is dim and still, a
 * failed engine smoulders, one that has never connected is a wireframe ghost.
 * Traffic is a nerve impulse: a request climbs from the organ up its nerve —
 * vagus, brachial plexus, spinal cord, optic or auditory nerve — into the
 * brain; the answer runs back down in the brain's colour. A relay between two
 * agents goes up one nerve and down the other, because that is what the bus
 * is: the brain in the middle, never a line from one hand to the other.
 *
 * Same public API as createAgentBus3D, so hud.js can swap one for the other on
 * the same canvas. Same budget too: no postprocessing, the loop parks itself
 * when the card is off-screen, every listener dies with dispose().
 */

import * as THREE from 'three';
import { AGENT_COLORS, colorOf, displayName, radialTexture } from './agentbus3d.js';
import { shellGeometry, BRAIN_CENTER } from './brainlayout.js';

const NERVE_COLOR = 0x5fb8ff;
const SKIN_COLOR = 0x4f9dff;

// ── the body's landmarks (units: the body stands ~6 tall, feet at -2.95) ──

const BRAIN_AT = new THREE.Vector3(0, 2.66, 0.01);
const STEM = [[0, 2.5, -0.05], [0, 2.3, -0.1]];
const CORD = [[0, 2.14, -0.15], [0, 1.82, -0.21], [0, 1.32, -0.25], [0, 0.82, -0.22], [0, 0.36, -0.2]];
const up = (fromY) => {
    // The cord from a given height back up to the brain, top-most last.
    const pts = CORD.filter(p => p[1] > fromY + 0.02).reverse();
    return [...pts, ...STEM.slice().reverse(), [BRAIN_AT.x, BRAIN_AT.y, BRAIN_AT.z]];
};
const VAGUS_UP = [[0.09, 1.86, 0.02], [0.07, 2.14, -0.02], [0.03, 2.4, -0.05], [BRAIN_AT.x, BRAIN_AT.y, BRAIN_AT.z]];

/* Organ slots. `pos` is where the organ sits and its traffic starts; `route`
 * is the nerve from there to the brain; `side` is which column of the chart
 * its name goes in (-1 left of the figure, +1 right) — see layoutLabels. */
const SLOTS = {
    heart:      { pos: [0.12, 1.42, 0.15],  side: 1,  route: () => [[0.12, 1.42, 0.15], [0.12, 1.62, 0.08], ...VAGUS_UP] },
    handR:      { pos: [-1.19, -0.2, 0.12], side: -1, route: () => armRoute(-1) },
    handL:      { pos: [1.19, -0.2, 0.12],  side: 1,  route: () => armRoute(1) },
    eyes:       { pos: [0, 2.56, 0.33],     side: -1, route: () => [[0.13, 2.56, 0.33], [0.05, 2.52, 0.16], [0, 2.56, 0.02], [BRAIN_AT.x, BRAIN_AT.y, BRAIN_AT.z]] },
    ears:       { pos: [0.36, 2.52, 0.0],   side: 1,  route: () => [[0.36, 2.52, 0.0], [0.2, 2.47, -0.04], [0.06, 2.46, -0.05], [BRAIN_AT.x, BRAIN_AT.y, BRAIN_AT.z]] },
    legL:       { pos: [0.34, -1.95, 0.02], side: 1,  route: () => legRoute(1) },
    legR:       { pos: [-0.34, -1.95, 0.02], side: -1, route: () => legRoute(-1) },
    lungs:      { pos: [-0.3, 1.55, 0.02],  side: -1, route: () => [[-0.3, 1.55, 0.02], [-0.12, 1.7, 0.0], [0.04, 1.8, 0.02], ...VAGUS_UP] },
    liver:      { pos: [-0.22, 1.02, 0.1],  side: -1, route: () => [[-0.22, 1.02, 0.1], [-0.05, 1.2, 0.04], [0.06, 1.55, 0.03], ...VAGUS_UP] },
    stomach:    { pos: [0.2, 0.97, 0.13],   side: 1,  route: () => [[0.2, 0.97, 0.13], [0.1, 1.2, 0.07], [0.08, 1.55, 0.04], ...VAGUS_UP] },
    kidneys:    { pos: [-0.22, 0.76, -0.14], side: -1, route: () => [[-0.22, 0.76, -0.14], [-0.08, 0.9, -0.2], ...up(0.9)] },
    intestines: { pos: [0, 0.44, 0.1],      side: 1,  route: () => [[0, 0.44, 0.1], [0.05, 0.8, 0.08], [0.08, 1.3, 0.05], ...VAGUS_UP] },
    spleen:     { pos: [0.37, 1.04, -0.05], side: 1,  route: () => [[0.37, 1.04, -0.05], [0.18, 1.2, -0.02], [0.08, 1.55, 0.03], ...VAGUS_UP] },
};
/** Where an organ is DRAWN, when that differs from where its name points —
 *  lungs and kidneys are pairs centred on the midline, but a leader line to
 *  the midline would point at the spine. */
const ORGAN_CENTER = { lungs: [0, 1.55, 0.02], kidneys: [0, 0.76, -0.14], ears: [0, 2.52, 0.0] };
function armRoute(s) {
    return [[s * 1.19, -0.2, 0.12], [s * 1.1, 0.08, 0.08], [s * 0.94, 0.96, -0.05],
            [s * 0.64, 1.88, -0.05], [s * 0.22, 2.02, -0.14], ...up(2.0)];
}
function legRoute(s) {
    return [[s * 0.35, -1.95, 0.02], [s * 0.34, -1.42, 0.03], [s * 0.3, -0.5, 0.0],
            [s * 0.26, 0.06, -0.06], [s * 0.1, 0.26, -0.18], ...up(0.3)];
}

const KNOWN = {
    claude: 'heart', codex: 'handR', cluadex: 'handL', gemini: 'eyes', grok: 'ears',
    unity: 'legL', unreal: 'legR',
};
const SPARE = ['lungs', 'liver', 'stomach', 'kidneys', 'intestines', 'spleen'];

// ── materials ────────────────────────────────────────────────────────────

const holoVert = /* glsl */`
    varying vec3 vN;
    varying vec3 vV;
    varying float vY;
    void main() {
        vec4 wp = modelMatrix * vec4(position, 1.0);
        vY = wp.y;
        vec4 mv = viewMatrix * wp;
        vN = normalize(normalMatrix * normal);
        vV = normalize(-mv.xyz);
        gl_Position = projectionMatrix * mv;
    }
`;
/* A scanner hologram: the rim glows, the body is faint, and a slice of light
 * sweeps down the body over and over, the way a scan reads a patient. */
const holoFrag = /* glsl */`
    precision highp float;
    varying vec3 vN;
    varying vec3 vV;
    varying float vY;
    uniform vec3  uColor;
    uniform float uGlow;
    uniform float uOpacity;
    uniform float uFill;
    uniform float uTime;
    uniform float uScan;
    void main() {
        float facing = abs(dot(normalize(vN), normalize(vV)));
        float rim = pow(1.0 - facing, 2.0);
        float sweep = 3.3 - mod(uTime * 0.85, 7.4);
        float band = exp(-pow((vY - sweep) / 0.06, 2.0)) * uScan;
        vec3 col = uColor * (0.55 + 0.7 * rim) * uGlow + vec3(0.55, 0.85, 1.0) * band;
        float a = (uFill + rim * 0.85) * uOpacity + band * 0.45 * uOpacity;
        gl_FragColor = vec4(col, a);
    }
`;

function holoMaterial(color, { opacity = 0.5, fill = 0.12, glow = 1, scan = 1, side = THREE.FrontSide } = {}) {
    return new THREE.ShaderMaterial({
        uniforms: {
            uColor: { value: new THREE.Color(color) },
            uGlow: { value: glow },
            uOpacity: { value: opacity },
            uFill: { value: fill },
            uTime: { value: 0 },
            uScan: { value: scan },
        },
        vertexShader: holoVert,
        fragmentShader: holoFrag,
        transparent: true,
        depthWrite: false,
        side,
        blending: THREE.AdditiveBlending,
    });
}

// ── geometry helpers ─────────────────────────────────────────────────────

const V = (a) => new THREE.Vector3(a[0], a[1], a[2]);

function ellipsoid(rx, ry, rz, seg = 20) {
    const g = new THREE.SphereGeometry(1, seg, Math.max(8, Math.round(seg * 0.7)));
    g.scale(rx, ry, rz);
    return g;
}

/** A tube along points with a radius that changes along it — limbs taper. */
function limb(points, r0, r1, tubular = 24, radial = 12) {
    const curve = new THREE.CatmullRomCurve3(points.map(V));
    const frames = curve.computeFrenetFrames(tubular, false);
    const pos = [], idx = [];
    for (let i = 0; i <= tubular; i++) {
        const t = i / tubular, P = curve.getPointAt(t);
        const N = frames.normals[i], B = frames.binormals[i];
        const r = r0 + (r1 - r0) * t;
        for (let j = 0; j <= radial; j++) {
            const a = (j / radial) * Math.PI * 2, c = Math.cos(a), s = Math.sin(a);
            pos.push(P.x + r * (c * N.x + s * B.x), P.y + r * (c * N.y + s * B.y), P.z + r * (c * N.z + s * B.z));
        }
    }
    for (let i = 0; i < tubular; i++) for (let j = 0; j < radial; j++) {
        const a = i * (radial + 1) + j, b = a + radial + 1;
        idx.push(a, b, a + 1, b, b + 1, a + 1);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setIndex(idx);
    g.computeVertexNormals();
    return g;
}

/** Pull a sphere's lower half into a point: the heart's apex, a lung's base. */
function taperDown(g, amount, pivotY = 0) {
    const p = g.getAttribute('position');
    for (let i = 0; i < p.count; i++) {
        const y = p.getY(i);
        if (y < pivotY) {
            const k = 1 - amount * Math.min(1, (pivotY - y) / 0.3);
            p.setX(i, p.getX(i) * k);
            p.setZ(i, p.getZ(i) * k);
        }
    }
    g.computeVertexNormals();
    return g;
}

// ── organ builders: each returns a Group of meshes sharing one material ──

const ORGAN_BUILD = {
    heart(mat) {
        const g = new THREE.Group();
        const body = new THREE.Mesh(taperDown(ellipsoid(0.15, 0.18, 0.13), 0.8, 0.02), mat);
        body.rotation.z = 0.45;           // apex down and to the body's left
        const aorta = new THREE.Mesh(new THREE.TorusGeometry(0.07, 0.025, 8, 16, Math.PI), mat);
        aorta.position.set(-0.02, 0.16, -0.02);
        aorta.rotation.y = Math.PI / 2;
        g.add(body, aorta);
        return g;
    },
    lungs(mat) {
        const g = new THREE.Group();
        for (const s of [1, -1]) {
            const m = new THREE.Mesh(ellipsoid(0.19, 0.34, 0.2), mat);
            m.position.set(s * 0.3, 0, 0);
            g.add(m);
        }
        return g;
    },
    liver(mat) {
        const m = new THREE.Mesh(ellipsoid(0.3, 0.15, 0.19), mat);
        m.rotation.z = -0.25;
        return new THREE.Group().add(m);
    },
    stomach(mat) {
        const path = [[0.08, 0.1, 0], [0.12, 0.0, 0.02], [0.06, -0.1, 0.03], [-0.08, -0.1, 0.02], [-0.16, -0.02, 0]];
        return new THREE.Group().add(new THREE.Mesh(limb(path, 0.1, 0.06, 16, 10), mat));
    },
    kidneys(mat) {
        const g = new THREE.Group();
        for (const s of [1, -1]) {
            const m = new THREE.Mesh(ellipsoid(0.07, 0.12, 0.06, 14), mat);
            m.position.set(s * 0.22, 0, 0);
            m.rotation.z = s * 0.2;
            g.add(m);
        }
        return g;
    },
    intestines(mat) {
        const pts = [];
        for (let i = 0; i <= 28; i++) {
            const t = i / 28, a = t * Math.PI * 7;
            pts.push([Math.cos(a) * 0.22 * (1 - t * 0.35), 0.16 - t * 0.32, Math.sin(a) * 0.08]);
        }
        return new THREE.Group().add(new THREE.Mesh(limb(pts, 0.045, 0.04, 90, 8), mat));
    },
    spleen(mat) {
        return new THREE.Group().add(new THREE.Mesh(ellipsoid(0.06, 0.12, 0.08, 14), mat));
    },
    eyes(mat) {
        const g = new THREE.Group();
        for (const s of [1, -1]) {
            const m = new THREE.Mesh(new THREE.SphereGeometry(0.06, 16, 12), mat);
            m.position.set(s * 0.13, 0, 0);
            const iris = new THREE.Mesh(new THREE.RingGeometry(0.018, 0.034, 20), mat);
            iris.position.set(s * 0.13, 0, 0.061);
            g.add(m, iris);
        }
        return g;
    },
    ears(mat) {
        const g = new THREE.Group();
        for (const s of [1, -1]) {
            const m = new THREE.Mesh(new THREE.TorusGeometry(0.07, 0.022, 8, 20), mat);
            m.position.set(s * 0.36, 0, 0);
            m.rotation.y = Math.PI / 2;
            m.scale.set(1, 1.35, 1);
            g.add(m);
        }
        return g;
    },
    hand(mat, s) {
        const g = new THREE.Group();
        g.add(new THREE.Mesh(ellipsoid(0.085, 0.11, 0.035, 14), mat));
        for (let f = 0; f < 5; f++) {
            const a = (f - 2) * 0.28;
            const len = f === 0 ? 0.1 : 0.14 - Math.abs(f - 2.5) * 0.012;
            const finger = new THREE.Mesh(new THREE.CapsuleGeometry(0.016, len, 3, 6), mat);
            const base = f === 0 ? new THREE.Vector3(s * 0.08, 0.02, 0.03) : new THREE.Vector3((f - 2.5) * 0.034 * s, -0.11, 0);
            finger.position.copy(base).add(new THREE.Vector3(Math.sin(a) * 0.05 * s, -Math.cos(a) * len * 0.5, 0));
            finger.rotation.z = f === 0 ? s * 0.9 : a * 0.35 * s;
            g.add(finger);
        }
        return g;
    },
    /* The whole leg is the organ — thigh and calf around the bone — drawn in
     * BODY coordinates, because unlike a viscus it is not a thing at a point. */
    leg(mat, s) {
        const g = new THREE.Group();
        g.add(new THREE.Mesh(limb([[s * 0.27, -0.02, 0], [s * 0.31, -0.7, 0.03], [s * 0.33, -1.38, 0.03]], 0.165, 0.115, 16, 12), mat));
        g.add(new THREE.Mesh(limb([[s * 0.33, -1.5, 0.03], [s * 0.34, -2.1, 0.0], [s * 0.35, -2.68, -0.02]], 0.115, 0.07, 16, 12), mat));
        return g;
    },
    ganglion(mat) {
        return new THREE.Group().add(new THREE.Mesh(new THREE.IcosahedronGeometry(0.06, 1), mat));
    },
};

function buildOrgan(slot, mat) {
    switch (slot) {
        case 'handR': return ORGAN_BUILD.hand(mat, -1);
        case 'handL': return ORGAN_BUILD.hand(mat, 1);
        case 'legL': return ORGAN_BUILD.leg(mat, 1);
        case 'legR': return ORGAN_BUILD.leg(mat, -1);
        default: return (ORGAN_BUILD[slot] ?? ORGAN_BUILD.ganglion)(mat);
    }
}
/** Organs drawn in body coordinates rather than around their anchor. */
const IN_BODY_FRAME = new Set(['legL', 'legR']);

// ── the factory ──────────────────────────────────────────────────────────

export function createAgentBody3D(canvas) {
    if (!canvas) return null;

    const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: true });
    renderer.setClearColor(0x000000, 0);
    renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 1.75));

    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(34, 1, 0.1, 100);
    const body = new THREE.Group();      // everything that sways together
    scene.add(body);

    /* Same camera model as the solar system: spherical around the body,
     * dragged by hand, wheel to zoom, double-click to go home. The default is
     * a slight three-quarter from the front — a body seen dead-on is a
     * diagram, one turned a little is a figure. */
    const HOME_THETA = 0.42, HOME_PHI = 0.1;
    const view = { theta: HOME_THETA, phi: HOME_PHI, dist: 11, target: new THREE.Vector3(0, 0.05, 0) };
    const DIST_MIN = 2.0, DIST_MAX = 22;
    const PHI_MIN = -0.55, PHI_MAX = 1.25;
    /* Same rule as the solar system: names are half-legible when the card
     * opens (alpha ≈ 0.58 at the fitted distance) and solid once zoomed. An
     * organ is a smaller thing than a planet, so the name matters more. */
    const LABEL_FAR_K = 1.35, LABEL_NEAR_K = 0.75;
    let fitted = 11;

    function applyCamera() {
        const r = view.dist, cp = Math.cos(view.phi), sp = Math.sin(view.phi);
        camera.position.set(
            view.target.x + r * cp * Math.sin(view.theta),
            view.target.y + r * sp,
            view.target.z + r * cp * Math.cos(view.theta));
        camera.lookAt(view.target);
    }
    applyCamera();

    // ── the body itself ──
    const skinMat = holoMaterial(SKIN_COLOR, { opacity: 0.34, fill: 0.05 });
    const skin = new THREE.Group();
    const addSkin = (geo, x = 0, y = 0, z = 0) => {
        const m = new THREE.Mesh(geo, skinMat);
        m.position.set(x, y, z);
        skin.add(m);
        return m;
    };
    addSkin(ellipsoid(0.33, 0.41, 0.37, 24), 0, 2.62, 0.02);
    addSkin(limb([[0, 2.1, -0.04], [0, 2.32, -0.02]], 0.12, 0.11, 4, 12));
    const torsoProfile = [[0.02, -0.07], [0.44, 0.0], [0.53, 0.26], [0.47, 0.64], [0.5, 0.96],
                          [0.59, 1.44], [0.66, 1.8], [0.58, 1.98], [0.28, 2.08], [0.12, 2.13]]
        .map(([r, y]) => new THREE.Vector2(r, y));
    const torso = addSkin(new THREE.LatheGeometry(torsoProfile, 28));
    torso.scale.z = 0.58;
    for (const s of [1, -1]) {
        addSkin(limb([[s * 0.64, 1.9, -0.03], [s * 0.93, 0.98, -0.06], [s * 1.1, 0.1, 0.06]], 0.12, 0.075));
        addSkin(ellipsoid(0.09, 0.13, 0.045, 12), s * 1.19, -0.2, 0.12);
        addSkin(limb([[s * 0.27, 0.02, 0], [s * 0.33, -1.42, 0.03], [s * 0.35, -2.72, -0.02]], 0.2, 0.08));
        addSkin(ellipsoid(0.09, 0.07, 0.19, 12), s * 0.36, -2.86, 0.1);
    }
    body.add(skin);

    // Skeleton: faint lines — spine, ribs, pelvis, the long bones.
    const bonePts = [];
    const seg = (a, b) => bonePts.push(V(a), V(b));
    for (let i = 0; i < 24; i++) {
        const y = 2.25 - i * 0.085;
        seg([-0.04, y, -0.27], [0.04, y, -0.27]);
    }
    for (let r = 0; r < 10; r++) {
        const y = 1.86 - r * 0.085, w = 0.34 + Math.sin((r / 9) * Math.PI) * 0.16;
        for (const s of [1, -1]) {
            let prev = null;
            for (let k = 0; k <= 10; k++) {
                const a = (k / 10) * Math.PI * 0.95;
                const p = [s * Math.sin(a) * w, y - k * 0.012, -0.25 + (1 - Math.cos(a)) * 0.25];
                if (prev) seg(prev, p);
                prev = p;
            }
        }
    }
    for (let k = 0; k < 24; k++) {
        const a0 = (k / 24) * Math.PI * 2, a1 = ((k + 1) / 24) * Math.PI * 2;
        seg([Math.cos(a0) * 0.4, 0.18 + Math.sin(a0 * 2) * 0.03, Math.sin(a0) * 0.18],
            [Math.cos(a1) * 0.4, 0.18 + Math.sin(a1 * 2) * 0.03, Math.sin(a1) * 0.18]);
    }
    for (const s of [1, -1]) {
        seg([s * 0.64, 1.9, -0.03], [s * 0.93, 0.98, -0.06]);
        seg([s * 0.93, 0.98, -0.06], [s * 1.1, 0.1, 0.06]);
        seg([s * 0.27, 0.05, 0], [s * 0.33, -1.42, 0.03]);
        seg([s * 0.33, -1.42, 0.03], [s * 0.35, -2.72, -0.02]);
        seg([0, 2.0, 0.1], [s * 0.62, 1.94, -0.05]);          // clavicle
    }
    const bones = new THREE.LineSegments(new THREE.BufferGeometry().setFromPoints(bonePts),
        new THREE.LineBasicMaterial({ color: 0x9fb8e8, transparent: true, opacity: 0.13, depthWrite: false, blending: THREE.AdditiveBlending }));
    body.add(bones);

    // The nervous system everyone has, occupied slot or not: cord, brachial
    // and sciatic trunks. Agents' own nerves are drawn per organ, over this.
    const trunk = [];
    const polyline = (pts) => { for (let i = 0; i < pts.length - 1; i++) trunk.push(V(pts[i]), V(pts[i + 1])); };
    polyline([...STEM, ...CORD, [0, 0.12, -0.18]]);
    for (const s of [1, -1]) {
        polyline(armRoute(s).slice(0, 5).reverse());
        polyline(legRoute(s).slice(0, 5).reverse().concat([[s * 0.36, -2.8, 0.06]]));
        for (let r = 0; r < 8; r++) {        // intercostal nerves
            const y = 1.8 - r * 0.1;
            polyline([[0, y, -0.22], [s * 0.3, y - 0.03, -0.12], [s * 0.42, y - 0.06, 0.08]]);
        }
    }
    const nerves = new THREE.LineSegments(new THREE.BufferGeometry().setFromPoints(trunk),
        new THREE.LineBasicMaterial({ color: NERVE_COLOR, transparent: true, opacity: 0.2, depthWrite: false, blending: THREE.AdditiveBlending }));
    body.add(nerves);

    // The brain — BrainX — in the head: the theme's own cortex, in miniature.
    const brainMat = holoMaterial(AGENT_COLORS.brain, { opacity: 0.75, fill: 0.18, glow: 1.3, scan: 0.4, side: THREE.DoubleSide });
    const brainGroup = new THREE.Group();
    const K = 0.0026;
    for (const side of ['L', 'R']) {
        const s = shellGeometry(side, 3);
        const g = new THREE.BufferGeometry();
        g.setAttribute('position', new THREE.BufferAttribute(s.positions, 3));
        g.setIndex(new THREE.BufferAttribute(s.index, 1));
        g.computeVertexNormals();
        const m = new THREE.Mesh(g, brainMat);
        m.scale.setScalar(K);
        m.position.set(-BRAIN_CENTER.x * K, -BRAIN_CENTER.y * K, -BRAIN_CENTER.z * K);
        brainGroup.add(m);
    }
    brainGroup.position.copy(BRAIN_AT);
    const corona = new THREE.Sprite(new THREE.SpriteMaterial({
        map: radialTexture(), color: AGENT_COLORS.brain,
        blending: THREE.AdditiveBlending, depthWrite: false, transparent: true, opacity: 0.8,
    }));
    corona.scale.setScalar(1.25);
    brainGroup.add(corona);
    body.add(brainGroup);

    const organs = new Map();    // agent name → organ
    const slotOf = new Map();    // agent name → slot key, stable across polls
    const motes = [];
    const MOTE_MAX = 60;
    const moteGeo = new THREE.SphereGeometry(0.05, 10, 8);
    const TRAIL_LEN = 22;
    /* World size of a name tag, matched to organLabelTexture's 256×48 canvas
     * so the text is not stretched. */
    const LABEL_H = 0.42, LABEL_W = LABEL_H * 256 / 48;

    let raf = 0, running = false, lastT = 0, clock = 0;

    // ── input: drag to turn, wheel to zoom, double-click home ──
    let dragging = false, lastX = 0, lastY = 0, userMoved = false;
    const listeners = new AbortController();
    const signal = listeners.signal;
    canvas.addEventListener('pointerdown', (e) => {
        dragging = true; userMoved = true;
        lastX = e.clientX; lastY = e.clientY;
        canvas.setPointerCapture?.(e.pointerId);
        e.stopPropagation(); e.preventDefault();
    }, { signal });
    canvas.addEventListener('pointermove', (e) => {
        if (!dragging) return;
        const w = canvas.clientWidth || 240;
        view.theta -= ((e.clientX - lastX) / w) * Math.PI * 2;
        view.phi = clamp(view.phi + ((e.clientY - lastY) / w) * Math.PI * 1.4, PHI_MIN, PHI_MAX);
        lastX = e.clientX; lastY = e.clientY;
        applyCamera();
        e.stopPropagation(); e.preventDefault();
    }, { signal });
    const endDrag = (e) => {
        if (!dragging) return;
        dragging = false;
        canvas.releasePointerCapture?.(e.pointerId);
        e.stopPropagation();
    };
    canvas.addEventListener('pointerup', endDrag, { signal });
    canvas.addEventListener('pointercancel', endDrag, { signal });
    canvas.addEventListener('wheel', (e) => {
        userMoved = true;
        view.dist = clamp(view.dist * (e.deltaY > 0 ? 1.12 : 0.89), DIST_MIN, DIST_MAX);
        applyCamera();
        e.stopPropagation(); e.preventDefault();
    }, { passive: false, signal });
    canvas.addEventListener('dblclick', (e) => {
        view.theta = HOME_THETA; view.phi = HOME_PHI;
        view.target.set(0, 0.05, 0);
        userMoved = false;
        refit();
        e.stopPropagation(); e.preventDefault();
    }, { signal });

    // ── public API ──

    function setAgents(list) {
        const seen = new Set();
        let rosterChanged = false;
        const ordered = [...list.filter(a => !a.moonOf), ...list.filter(a => a.moonOf)];
        for (const a of ordered) {
            const name = a.name;
            seen.add(name);
            let o = organs.get(name);
            if (!o) {
                const host = a.moonOf ? organs.get(a.moonOf) : null;
                o = host ? buildMoon(name, a.moonOf, host) : buildOrganFor(name);
                organs.set(name, o);
                rosterChanged = true;
            }
            applyPresence(o, a);
        }
        for (const [name, o] of organs) {
            const hostGone = o.hostName && !seen.has(o.hostName);
            if (seen.has(name) && !hostGone) continue;
            for (const part of [o.root, o.nerve, o.label, o.leader]) {
                if (!part) continue;
                part.parent?.remove(part);
                disposeDeep(part);
            }
            organs.delete(name);
            // A slot is freed with its agent, so the next newcomer can have it.
            if (!o.isMoon) slotOf.delete(name);
            rosterChanged = true;
        }
        // Presence is re-sent every couple of seconds; the columns only move
        // when someone joins or leaves.
        if (rosterChanged) layoutLabels();
    }

    function fireTraffic(name, inbound = true, forceColor = null) {
        const o = organs.get(name);
        if (!o || motes.length >= MOTE_MAX) return;
        const color = forceColor ?? (inbound ? colorOf(name) : AGENT_COLORS.brain);
        const mesh = new THREE.Mesh(moteGeo, new THREE.MeshBasicMaterial({
            color, blending: THREE.AdditiveBlending, transparent: true, depthWrite: false,
        }));
        body.add(mesh);
        const trailGeo = new THREE.BufferGeometry();
        trailGeo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(TRAIL_LEN * 3), 3));
        trailGeo.setAttribute('color', new THREE.BufferAttribute(new Float32Array(TRAIL_LEN * 3), 3));
        const trail = new THREE.Line(trailGeo, new THREE.LineBasicMaterial({
            vertexColors: true, blending: THREE.AdditiveBlending, transparent: true, depthWrite: false,
        }));
        trail.frustumCulled = false;
        body.add(trail);
        motes.push({ mesh, trail, organ: o, t: 0, dur: o.dur, inbound, head: new THREE.Color(color), history: [] });
    }

    /** Up one nerve into the brain, then down the other — see the file header. */
    function fireRelay(fromName, toName) {
        const src = organs.get(fromName), dst = organs.get(toName);
        if (!src && !dst) return;
        const legs = (src ? 1 : 0) + (dst ? 1 : 0);
        if (motes.length + legs > MOTE_MAX) return;
        const color = colorOf(src ? fromName : toName);
        if (src) fireTraffic(fromName, true, color);
        if (!dst) return;
        const handoff = src ? src.dur * 1000 : 0;
        setTimeout(() => { if (running) fireTraffic(toName, false, color); }, handoff);
    }

    /* The figure is fitted, the names are not: head to feet must always be
     * in frame, arms too, and then the name columns take whatever width the
     * card has left. A narrow card squeezes the columns in toward the arms
     * rather than shrinking the whole body to make room for words. */
    function fitDist() {
        const t = Math.tan(camera.fov * Math.PI / 360);
        const halfH = 3.2, halfW = 1.45;
        const aspect = Math.max(0.2, camera.aspect);
        const vertical = halfH / t;
        const horizontal = halfW / (t * aspect) * 1.1;
        fitted = clamp(Math.max(vertical, horizontal), DIST_MIN, DIST_MAX);
        const visibleHalfW = fitted * t * aspect;
        labelColX = clamp(visibleHalfW - LABEL_W / 2 - 0.12, 1.45, 2.7);
        layoutLabels();
        return fitted;
    }
    function refit() {
        if (userMoved) return;
        view.dist = fitDist();
        applyCamera();
    }
    function resize() {
        const box = canvas.getBoundingClientRect();
        const w = box.width || canvas.clientWidth || 240;
        const h = box.height || canvas.clientHeight || 150;
        if (w < 4 || h < 4) return;
        renderer.setSize(Math.round(w), Math.round(h), false);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
        refit();
    }
    function start() { if (!running) { running = true; lastT = performance.now(); raf = requestAnimationFrame(tick); } }
    function stop() { running = false; cancelAnimationFrame(raf); }
    function dispose() {
        stop();
        listeners.abort();
        motes.forEach(m => { disposeDeep(m.mesh); disposeDeep(m.trail); });
        motes.length = 0;
        disposeDeep(scene);
        renderer.dispose();
    }

    // ── internals ──

    function nextSlot(name) {
        if (slotOf.has(name)) return slotOf.get(name);
        const taken = new Set(slotOf.values());
        let slot = KNOWN[String(name).toLowerCase()];
        if (!slot || taken.has(slot)) slot = SPARE.find(s => !taken.has(s));
        if (!slot) {
            // Past the viscera: ganglia down the sympathetic chain, one per agent.
            let k = 0;
            while (taken.has('ganglion' + k)) k++;
            slot = 'ganglion' + k;
        }
        slotOf.set(name, slot);
        return slot;
    }

    function slotDef(slot) {
        if (SLOTS[slot]) return SLOTS[slot];
        const k = +slot.slice(8) || 0;
        const s = k % 2 ? -1 : 1;
        const y = 1.9 - Math.floor(k / 2) * 0.22;
        return { pos: [s * 0.15, y, -0.22], side: s, route: () => [[s * 0.15, y, -0.22], [0, y + 0.02, -0.22], ...up(y)] };
    }

    function buildOrganFor(name) {
        const slot = nextSlot(name);
        const def = slotDef(slot);
        const color = colorOf(name);
        // A limb is a big organ: at a viscus's fill it reads as a solid pipe
        // (Unity's silver leg was a white tube), so limbs carry mostly rim.
        const big = IN_BODY_FRAME.has(slot);
        const mat = holoMaterial(color, { opacity: big ? 0.6 : 0.8, fill: big ? 0.05 : 0.2, glow: 1 });
        const root = new THREE.Group();
        const shape = buildOrgan(slot, mat);
        root.add(shape);
        // Most organs are built around their own centre and placed there; a
        // leg is built where it stands and keeps the body's origin.
        if (!IN_BODY_FRAME.has(slot)) root.position.copy(V(ORGAN_CENTER[slot] ?? def.pos));
        body.add(root);

        const route = def.route();
        const curve = new THREE.CatmullRomCurve3(route.map(V), false, 'centripetal');
        const nerve = new THREE.Line(new THREE.BufferGeometry().setFromPoints(curve.getSpacedPoints(64)),
            new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.2, depthWrite: false, blending: THREE.AdditiveBlending }));
        body.add(nerve);

        const side = def.side ?? (def.pos[0] >= 0 ? 1 : -1);
        const { label, leader } = makeLabel(name, color, side);
        const len = curve.getLength();
        return {
            slot, root, shape, mat, nerve, label, leader, curve, color, side,
            // Longer nerves carry slower trips, within a band a glance can follow.
            dur: clamp(0.45 + len * 0.13, 0.5, 1.15),
            online: false, everSeen: false, state: 'never', isMoon: false,
            pos: new THREE.Vector3(...def.pos),
        };
    }

    /* ── the chart's labels ──
     * Names sit in two columns either side of the figure, each on a leader
     * line to its organ, the way an anatomy plate is lettered. The card is
     * wide and the body is tall, so the columns use width the figure leaves
     * empty — which is what lets the names be big enough to read without
     * zooming, where names crowded on the organs could only be smudges. */
    /** Column centre, re-derived from the card's shape on every resize: as
     *  far out as the card allows, never so far a name leaves the frame. */
    let labelColX = 2.3;
    const LABEL_GAP = 0.48;
    function makeLabel(name, color, side) {
        const label = new THREE.Sprite(new THREE.SpriteMaterial({
            map: organLabelTexture(name, color, side), transparent: true,
            depthWrite: false, depthTest: false, opacity: 0,
        }));
        label.scale.set(LABEL_W, LABEL_H, 1);
        label.visible = false;
        body.add(label);
        const leader = new THREE.Line(
            new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(), new THREE.Vector3(), new THREE.Vector3()]),
            new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0, depthWrite: false, blending: THREE.AdditiveBlending }));
        leader.frustumCulled = false;
        body.add(leader);
        return { label, leader };
    }

    /** Stack each column top to bottom in the order the organs sit, pushing a
     *  name down only as far as it needs to clear the one above it. */
    function layoutLabels() {
        for (const side of [-1, 1]) {
            const col = [...organs.values()].filter(o => o.side === side)
                .sort((a, b) => (b.isMoon ? b.host.pos.y - 0.01 : b.pos.y) - (a.isMoon ? a.host.pos.y - 0.01 : a.pos.y));
            let floor = 3.1;
            for (const o of col) {
                const want = o.isMoon ? o.host.pos.y - 0.2 : o.pos.y;
                const y = Math.min(want, floor);
                floor = y - LABEL_GAP;
                o.labelY = y;
                o.label.position.set(side * labelColX, y, 0.05);
            }
        }
    }

    function updateLeader(o, a) {
        const p = o.leader.geometry.attributes.position;
        const edge = o.side * (labelColX - LABEL_W / 2 + 0.06);
        const elbow = o.side * Math.max(Math.abs(o.pos.x) + 0.25, 1.35);
        p.setXYZ(0, o.pos.x, o.pos.y, o.pos.z);
        p.setXYZ(1, elbow, o.labelY, 0.05);
        p.setXYZ(2, edge, o.labelY, 0.05);
        p.needsUpdate = true;
        o.leader.material.opacity = a * 0.45;
        o.leader.visible = a > 0.01;
    }

    function buildMoon(name, hostName, host) {
        const color = colorOf(name);
        const mat = holoMaterial(color, { opacity: 0.9, fill: 0.3, glow: 1 });
        const root = new THREE.Group();
        root.add(new THREE.Mesh(new THREE.IcosahedronGeometry(0.045, 1), mat));
        body.add(root);
        const tether = new THREE.Line(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(), new THREE.Vector3()]),
            new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.35, depthWrite: false, blending: THREE.AdditiveBlending }));
        tether.frustumCulled = false;
        body.add(tether);
        const { label, leader } = makeLabel(name, color, host.side);
        return {
            slot: 'ganglion·' + host.slot, root, shape: root, mat, nerve: tether, label, leader, color,
            host, hostName, isMoon: true, side: host.side, phase: Math.random() * Math.PI * 2,
            dur: host.dur + 0.15, online: false, everSeen: false, state: 'never',
            pos: new THREE.Vector3().copy(host.pos),
        };
    }

    /* The same six states the host decides for the planets (BusNodeState in
     * MainWindow.AgentBus.cs), read as tissue instead of light. */
    const GLOW = { live: 1.55, ready: 0.8, idle: 0.45, fault: 0.5, down: 0.38, off: 0.22, never: 0.25 };
    const NERVE_OPACITY = { live: 0.55, ready: 0.3, idle: 0.18, fault: 0.14, down: 0.12, off: 0.06, never: 0.07 };

    function applyPresence(o, a) {
        const state = a.state || (a.online ? 'live' : (a.everSeen !== false ? 'idle' : 'never'));
        o.state = state;
        o.online = state === 'live';
        o.everSeen = a.everSeen !== false;
        const ghost = state === 'never' || state === 'off';
        const u = o.mat.uniforms;
        if (ghost) u.uColor.value.setHex(0x6b7196);
        else if (state === 'fault' || state === 'down') u.uColor.value.setHex(o.color).lerp(new THREE.Color(0x8a2e1c), 0.65);
        else u.uColor.value.setHex(o.color);
        o.baseGlow = GLOW[state] ?? 0.4;
        u.uGlow.value = o.baseGlow;
        const big = IN_BODY_FRAME.has(o.slot);
        u.uFill.value = ghost ? 0.02 : (state === 'live' ? (big ? 0.06 : 0.24) : (big ? 0.03 : 0.12));
        o.mat.wireframe = ghost;
        if (o.nerve) {
            o.nerve.material.opacity = NERVE_OPACITY[state] ?? 0.1;
            o.nerve.material.color.setHex(ghost ? NERVE_COLOR : o.color);
        }
    }

    function updateTrail(m, fade) {
        m.history.unshift(m.mesh.position.clone());
        while (m.history.length > TRAIL_LEN) m.history.pop();
        const pos = m.trail.geometry.attributes.position;
        const col = m.trail.geometry.attributes.color;
        for (let i = 0; i < TRAIL_LEN; i++) {
            const p = m.history[Math.min(i, m.history.length - 1)];
            pos.setXYZ(i, p.x, p.y, p.z);
            const t = 1 - i / (TRAIL_LEN - 1);
            const a = t * t * fade;
            col.setXYZ(i, m.head.r * a, m.head.g * a, m.head.b * a);
        }
        pos.needsUpdate = true;
        col.needsUpdate = true;
    }

    const _p = new THREE.Vector3();
    /** Where an impulse is at progress k (0 = at the organ, 1 = in the brain). */
    function pathPoint(o, k, out) {
        // Curve.getPointAt outside [0, 1] indexes past its arc-length table and
        // throws — which, inside the render loop, is the loop.
        k = clamp(k, 0, 1);
        if (o.isMoon) {
            // A hop from the ganglion to the organ it serves, then that organ's nerve.
            if (k < 0.18) return out.lerpVectors(o.pos, o.host.pos, k / 0.18);
            return o.host.curve.getPointAt((k - 0.18) / 0.82, out);
        }
        return o.curve.getPointAt(k, out);
    }

    function tick(now) {
        if (!running) return;
        // Scheduled FIRST: if anything below ever throws, the card keeps
        // animating instead of freezing on the frame that failed, with
        // `running` still claiming it is alive.
        raf = requestAnimationFrame(tick);
        // A rAF timestamp is the frame's START, and the first one after
        // start() can predate the performance.now() taken there — a negative
        // dt that ran every impulse backwards off the start of its nerve.
        const dt = clamp((now - lastT) / 1000, 0, 0.05);
        lastT = now;
        clock += dt;

        // The body sways a little on its own, like a figure on a scanner bed
        // being turned — a still body in a live panel reads as a frozen one.
        body.rotation.y = Math.sin(clock * 0.22) * 0.32;

        for (const m of [skinMat, brainMat]) m.uniforms.uTime.value = clock;
        const breath = 1 + Math.sin(clock * 1.25) * 0.035;
        brainGroup.scale.setScalar(breath);
        corona.material.opacity = 0.55 + Math.sin(clock * 1.6) * 0.15;

        const far = fitted * LABEL_FAR_K, near = fitted * LABEL_NEAR_K;
        const labelAlpha = clamp((far - view.dist) / (far - near), 0, 1);

        for (const o of organs.values()) {
            o.mat.uniforms.uTime.value = clock;
            if (o.isMoon) continue;
            // Rhythm is life: only a live organ keeps it. The heart beats and
            // the lungs breathe in size; everything else pulses in light, since
            // a hand or a leg that swelled would look wrong rather than alive.
            let s = 1, glow = o.baseGlow;
            if (o.online) {
                if (o.slot === 'heart') {
                    const beat = (clock * 1.2) % 1;       // ~72 bpm, lub-dub
                    s = 1 + 0.12 * Math.exp(-Math.pow((beat - 0.08) / 0.05, 2)) + 0.07 * Math.exp(-Math.pow((beat - 0.28) / 0.05, 2));
                } else if (o.slot === 'lungs') {
                    s = 1 + Math.sin(clock * 1.6) * 0.05;
                } else {
                    glow = o.baseGlow * (1 + 0.18 * Math.sin(clock * 2.2 + o.dur * 7));
                }
            }
            if (!IN_BODY_FRAME.has(o.slot)) o.shape.scale.setScalar(s);
            o.mat.uniforms.uGlow.value = glow;
            const a = o.everSeen ? labelAlpha : labelAlpha * 0.55;
            o.label.visible = a > 0.01;
            o.label.material.opacity = a;
            updateLeader(o, a);
        }
        for (const o of organs.values()) {
            if (!o.isMoon) continue;
            o.phase += dt * (o.online ? 1.35 : 0.45);
            o.pos.set(o.host.pos.x + Math.cos(o.phase) * 0.24,
                      o.host.pos.y + Math.sin(o.phase * 0.7) * 0.08,
                      o.host.pos.z + Math.sin(o.phase) * 0.24);
            o.root.position.copy(o.pos);
            const tp = o.nerve.geometry.attributes.position;
            tp.setXYZ(0, o.pos.x, o.pos.y, o.pos.z);
            tp.setXYZ(1, o.host.pos.x, o.host.pos.y, o.host.pos.z);
            tp.needsUpdate = true;
            const a = o.everSeen ? labelAlpha : labelAlpha * 0.55;
            o.label.visible = a > 0.01;
            o.label.material.opacity = a;
            updateLeader(o, a);
        }

        for (let i = motes.length - 1; i >= 0; i--) {
            const m = motes[i];
            m.t += dt / m.dur;
            if (m.t >= 1) {
                body.remove(m.mesh, m.trail);
                disposeDeep(m.mesh); disposeDeep(m.trail);
                motes.splice(i, 1);
                continue;
            }
            const k = m.inbound ? m.t : 1 - m.t;
            pathPoint(m.organ, k, _p);
            // Afferent and efferent traffic ride either side of the same nerve,
            // so a request and its answer passing never merge into one blob.
            _p.x += m.inbound ? 0.022 : -0.022;
            m.mesh.position.copy(_p);
            const fade = m.t < 0.12 ? m.t / 0.12 : (m.t > 0.9 ? (1 - m.t) / 0.1 : 1);
            m.mesh.material.opacity = fade;
            updateTrail(m, fade);
        }

        renderer.render(scene, camera);
    }

    resize();
    start();

    /** Live numbers for tests and consoles — pixels cannot be read back from
     *  a composited WebGL canvas, positions and states can. */
    function debugState() {
        return {
            running,
            kind: 'body',
            view: { theta: +view.theta.toFixed(3), phi: +view.phi.toFixed(3), dist: +view.dist.toFixed(2), userMoved },
            labelsVisible: [...organs.values()].filter(o => o.label?.visible).length,
            trails: motes.length,
            planets: [...organs.entries()].map(([name, o]) => ({
                name, organ: o.slot, state: o.state, online: o.online,
                moonOf: o.hostName ?? null,
                glow: +o.mat.uniforms.uGlow.value.toFixed(2),
                ghost: !!o.mat.wireframe,
                x: +o.pos.x.toFixed(3), y: +o.pos.y.toFixed(3),
            })),
            motes: motes.length,
        };
    }

    return { setAgents, fireTraffic, fireRelay, resize, start, stop, dispose, debugState };
}

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

/** A name for the chart's columns: text hugging the side nearest the body,
 *  where its leader line lands. Drawn larger in its canvas than the planets'
 *  tags (32 px in 48, not 30 in 64) because it has to read at card size. */
function organLabelTexture(name, color, side) {
    const w = 256, h = 48;
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    const ctx = c.getContext('2d');
    ctx.font = '600 32px "JetBrains Mono", "Cascadia Mono", Consolas, monospace';
    ctx.textBaseline = 'middle';
    ctx.textAlign = side > 0 ? 'left' : 'right';
    const x = side > 0 ? 6 : w - 6;
    const text = displayName(name);
    ctx.lineWidth = 6;
    ctx.strokeStyle = 'rgba(0,0,0,0.85)';
    ctx.strokeText(text, x, h / 2);
    ctx.fillStyle = '#' + color.toString(16).padStart(6, '0');
    ctx.fillText(text, x, h / 2);
    const tex = new THREE.CanvasTexture(c);
    tex.colorSpace = THREE.SRGBColorSpace;
    return tex;
}

function disposeDeep(obj) {
    obj?.traverse?.((o) => {
        o.geometry?.dispose?.();
        const mats = Array.isArray(o.material) ? o.material : (o.material ? [o.material] : []);
        for (const m of mats) { m.map?.dispose?.(); m.dispose?.(); }
    });
}
