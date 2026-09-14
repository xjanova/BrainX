// BrainX Neural Brain — the visible brain.
//
// brainlayout.js decides where everything is; this file turns those numbers
// into things the GPU can draw, and owns the brain theme's decor:
//
//   anatomy     translucent cortex (both hemispheres, folded), cerebellum,
//               brainstem and the deep nuclei, shaded like a scan: rims
//               glow, sulci sink dark, crowns catch a headlamp
//   tissue      thousands of faint cortical neurons that are NOT notes, and
//               ~2,100 white-matter tracts that are not links — the brain's
//               own wiring, which the vault's fibres then join
//   fibres      the vault's wiki-links as curved axons, with an action
//               potential that runs down the fibre when a note is touched
//   up close    within ~70 units of the camera, neurons grow dendrites,
//               an apical tuft and an axon (the "zoom in and it is cells"
//               half of the brief)
//
// scene.js keeps owning the camera, physics, pulses and selection. The
// contract between the two is small on purpose: scene hands over a mounted
// universe and asks for objects; this file never reaches back into scene.

import * as THREE from 'three';
import { hashStr } from './layout.js';
import {
    shellGeometry, gliaPoints, tractPolylines, fiberRoute, sampleRoute,
    neuronTemplate, STEM_PATH, stemRadius,
} from './brainlayout.js';

/** Segments per note fibre. 8 keeps a callosal arch smooth while 12.6k
 *  fibres still fit in one indexed buffer (9 vertices each). */
const FIBER_S = 8;
const FIBER_V = FIBER_S + 1;
/** Fibres re-sampled per frame while the layout settles. Sampling every one
 *  of ~12.6k each frame costs ~16 ms, which is the whole frame; a rotating
 *  budget lets the fibres trail the neurons by a frame or two, which nobody
 *  can see, instead of dropping the settle to half rate, which everybody can. */
const FIBER_BUDGET = 3500;

// ── shaders ──────────────────────────────────────────────────────────────

const cortexVert = /* glsl */`
    attribute float aFold;
    attribute float aMedial;
    varying float vFold;
    varying float vMedial;
    varying vec3  vN;
    varying vec3  vV;
    varying float vY;
    varying float vDepth;
    void main() {
        vFold = aFold;
        vMedial = aMedial;
        vec4 wp = modelMatrix * vec4(position, 1.0);
        vY = position.y;
        vec4 mv = viewMatrix * wp;
        vDepth = -mv.z;
        vN = normalize(normalMatrix * normal);
        vV = normalize(-mv.xyz);
        gl_Position = projectionMatrix * mv;
    }
`;
const cortexFrag = /* glsl */`
    precision highp float;
    varying float vFold;
    varying float vMedial;
    varying vec3  vN;
    varying vec3  vV;
    varying float vY;
    varying float vDepth;
    uniform vec3  uTint;
    uniform vec3  uSulcus;
    uniform float uOpacity;
    uniform float uTime;
    uniform float uScan;
    uniform float uBack;
    uniform float uFadeBelow;   // the spinal cord trails off instead of ending in a cut
    void main() {
        vec3 n = normalize(vN);
        if (!gl_FrontFacing) n = -n;
        // Clamped: two normalised vectors can dot to 1.0000001, and pow() of
        // the negative base that leaves is NaN. Three such pixels, spread by
        // the bloom's mip chain, blacked out half the frame.
        float facing = clamp(abs(dot(n, normalize(vV))), 0.0, 1.0);
        // The medial walls are flat and face each other: seen edge-on, their
        // rims stack into a white sheet down the fissure. They keep a trace
        // of rim so the fissure still reads as an edge.
        float rim = pow(1.0 - facing, 2.3) * (1.0 - 0.85 * vMedial);
        float crown = smoothstep(0.12, 0.95, vFold);
        vec3 col = mix(uSulcus, uTint, crown);
        // A headlamp from above the camera: the folds get relief even when the
        // brain is seen head-on, where the rim term alone is flat.
        float lamp = clamp(dot(n, normalize(vec3(0.25, 0.75, 0.6))), 0.0, 1.0);
        float body = (0.02 + 0.2 * crown * crown) * (0.35 + 0.9 * lamp);
        float a = (body + rim * 0.6) * uOpacity * (gl_FrontFacing ? 1.0 : uBack);
        a *= smoothstep(uFadeBelow, uFadeBelow + 26.0, vY);
        // Up close the cortex steps aside: a surface the camera is almost
        // touching is seen at a grazing angle, its rim fills half the screen,
        // and it hides the very neurons the camera came in to look at.
        a *= smoothstep(6.0, 48.0, vDepth);
        // The scanner's slice: one bright band drifting up and down the brain,
        // the way an MRI viewer scrubs through a volume.
        float slice = sin(uTime * 0.21) * 95.0 + 10.0;
        // Squared by hand — pow() of a negative base is undefined in GLSL.
        float sd = (vY - slice) / 2.2;
        float band = exp(-sd * sd) * uScan * uOpacity;
        gl_FragColor = vec4(col + vec3(0.45, 0.8, 1.0) * band * 1.2, a + band * 0.2);
    }
`;

/* Fibres: a curve per link with an action potential. aSpark is per-edge
 * (x = start time, y = direction, +1 from end A, -1 from end B); aT is how far
 * along the fibre a vertex sits. The spike is a bright head with a tail that
 * trails back toward the neuron that fired — so the eye reads which way it
 * travelled without an arrowhead. */
const fiberVert = /* glsl */`
    attribute vec3  aColor;
    attribute float aAlpha;
    attribute float aT;
    attribute vec2  aSpark;
    varying vec3  vColor;
    varying float vAlpha;
    varying float vT;
    varying vec2  vSpark;
    void main() {
        vColor = aColor; vAlpha = aAlpha; vT = aT; vSpark = aSpark;
        gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }
`;
const fiberFrag = /* glsl */`
    precision highp float;
    varying vec3  vColor;
    varying float vAlpha;
    varying float vT;
    varying vec2  vSpark;
    uniform float uTime;
    uniform float uMotion;
    uniform float uStarScale;
    uniform float uEdgeAlpha;
    uniform float uSpark;
    uniform float uSparkSpeed;
    void main() {
        float breathe = 1.0 + uMotion * 0.12 * sin(uTime * 0.55);
        // Fibres thin out into the soma instead of piling up in a bright knot
        // at every neuron with many links.
        float taper = smoothstep(0.0, 0.07, vT) * smoothstep(1.0, 0.93, vT);
        float a = vAlpha * breathe * (0.35 + 0.65 * taper);
        vec3 col = vColor;

        float age = uTime - vSpark.x;
        float s = vSpark.y > 0.0 ? vT : 1.0 - vT;
        float front = age * uSparkSpeed;
        float live = step(0.0, age) * (1.0 - smoothstep(1.02, 1.3, front));
        float hd = (s - front) / 0.045;   // behind the front it is negative: no pow()
        float head = exp(-hd * hd);
        float tail = s < front ? exp(-(front - s) / 0.14) * 0.6 : 0.0;
        float ap = (head + tail) * live * uSpark;
        col += vec3(0.85, 0.95, 1.25) * ap * 1.5;
        a += ap * 0.9;

        gl_FragColor = vec4(col * uStarScale, a * uStarScale * uEdgeAlpha);
    }
`;

/* The brain's own tracts: static, DTI-coloured, with a slow shimmer that
 * runs along each fibre so the wiring looks conducted rather than drawn. */
const tractVert = /* glsl */`
    attribute vec3  aColor;
    attribute float aT;
    attribute float aSeed;
    varying vec3  vColor;
    varying float vT;
    varying float vSeed;
    void main() {
        vColor = aColor; vT = aT; vSeed = aSeed;
        gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }
`;
const tractFrag = /* glsl */`
    precision highp float;
    varying vec3  vColor;
    varying float vT;
    varying float vSeed;
    uniform float uTime;
    uniform float uOpacity;
    uniform float uMotion;
    void main() {
        float taper = smoothstep(0.0, 0.1, vT) * smoothstep(1.0, 0.9, vT);
        float flow = 0.75 + 0.25 * sin(vT * 18.0 - uTime * 1.6 * uMotion + vSeed * 40.0);
        gl_FragColor = vec4(vColor, uOpacity * taper * flow);
    }
`;

/* A neuron up close. aOrder fades distal branches (0 = trunk), aAxon picks
 * the axon out in a whiter tone; a travelling glow runs out along the tree
 * when the note fires. */
const dendriteVert = /* glsl */`
    attribute float aOrder;
    attribute float aAxon;
    varying float vOrder;
    varying float vAxon;
    varying float vDist;
    void main() {
        vOrder = aOrder; vAxon = aAxon;
        vDist = length(position);
        gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }
`;
const dendriteFrag = /* glsl */`
    precision highp float;
    varying float vOrder;
    varying float vAxon;
    varying float vDist;
    uniform vec3  uColor;
    uniform float uOpacity;
    uniform float uFire;     // seconds since the note last fired, < 0 = never
    uniform float uTime;
    void main() {
        float fade = 1.0 - vOrder * 0.55;
        // Pulled toward white: a 1-px line in the region's own neon colour
        // disappears against fibres of that same colour behind it.
        vec3 col = mix(mix(uColor, vec3(1.0), 0.35), vec3(0.85, 0.92, 1.0), vAxon * 0.55);
        // Firing: light spreading outward from the soma along the arbour.
        float live = step(0.0, uFire) * (1.0 - smoothstep(0.7, 1.2, uFire));
        float wd = (vDist - uFire * 18.0) / 2.5;   // negative inside the front: no pow()
        float wave = exp(-wd * wd) * live;
        float shimmer = 0.85 + 0.15 * sin(vDist * 2.4 - uTime * 3.0);
        float a = uOpacity * fade * shimmer * (vAxon > 0.5 ? 0.55 : 0.8) + wave * uOpacity * 0.9;
        gl_FragColor = vec4(col + vec3(1.0) * wave * 0.8, a);
    }
`;

const backdropVert = /* glsl */`
    varying vec3 vDir;
    void main() {
        vDir = normalize(position);
        gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }
`;
const backdropFrag = /* glsl */`
    precision highp float;
    varying vec3 vDir;
    void main() {
        float h = vDir.y;
        vec3 deep = vec3(0.006, 0.010, 0.024);
        vec3 mid  = vec3(0.020, 0.034, 0.070);
        vec3 col = mix(mid, deep, smoothstep(0.0, 0.85, abs(h + 0.08)));
        // A faint violet haze low on the horizon, like the glow of a lightbox
        // behind a scan.
        float haze = pow(max(0.0, 1.0 - abs(h + 0.12) * 1.6), 3.0);
        col += vec3(0.030, 0.012, 0.050) * haze;
        gl_FragColor = vec4(col, 1.0);
    }
`;

// ── helpers ──────────────────────────────────────────────────────────────

let _dotTex = null;
function dotTexture() {
    if (_dotTex) return _dotTex;
    const S = 64, c = document.createElement('canvas');
    c.width = c.height = S;
    const g = c.getContext('2d').createRadialGradient(S / 2, S / 2, 0, S / 2, S / 2, S / 2);
    g.addColorStop(0, 'rgba(255,255,255,1)');
    g.addColorStop(0.35, 'rgba(255,255,255,0.45)');
    g.addColorStop(1, 'rgba(255,255,255,0)');
    const ctx = c.getContext('2d');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, S, S);
    _dotTex = new THREE.CanvasTexture(c);
    _dotTex.colorSpace = THREE.SRGBColorSpace;
    return _dotTex;
}

/** DTI convention: left-right red, front-back green, up-down blue. */
function dtiColor(dx, dy, dz, out, o) {
    let r = Math.abs(dx), g = Math.abs(dz), b = Math.abs(dy);
    const m = Math.max(r, g, b) || 1;
    r /= m; g /= m; b /= m;
    // Lift the floor so a diagonal fibre is a mixed hue, not a muddy dark one.
    out[o] = 0.18 + 0.82 * r; out[o + 1] = 0.18 + 0.82 * g; out[o + 2] = 0.22 + 0.78 * b;
}

function cortexMaterial(opts = {}) {
    return new THREE.ShaderMaterial({
        uniforms: {
            uTint:    { value: new THREE.Color(opts.tint ?? 0x9fb4ff) },
            uSulcus:  { value: new THREE.Color(opts.sulcus ?? 0x241a52) },
            uOpacity: { value: opts.opacity ?? 0.5 },
            uTime:    { value: 0 },
            uScan:    { value: opts.scan ?? 1 },
            uBack:    { value: opts.back ?? 0.35 },
            uFadeBelow: { value: opts.fadeBelow ?? -1e4 },
        },
        vertexShader: cortexVert,
        fragmentShader: cortexFrag,
        transparent: true,
        depthWrite: false,
        side: THREE.DoubleSide,
        blending: THREE.AdditiveBlending,
    });
}

function geometryFromShell(s) {
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(s.positions, 3));
    geo.setAttribute('aFold', new THREE.BufferAttribute(s.fold, 1));
    geo.setAttribute('aMedial', new THREE.BufferAttribute(s.medial, 1));
    geo.setIndex(new THREE.BufferAttribute(s.index, 1));
    geo.computeVertexNormals();
    return geo;
}

/** A tube whose radius changes along its length — TubeGeometry only does one
 *  radius, and the brainstem is all about the bulge of the pons. */
function taperedTube(points, radiusAt, tubular = 56, radial = 22, foldAt = null) {
    const curve = new THREE.CatmullRomCurve3(points.map(p => new THREE.Vector3(p.x, p.y, p.z)));
    const frames = curve.computeFrenetFrames(tubular, false);
    const pos = [], fold = [], idx = [];
    for (let i = 0; i <= tubular; i++) {
        const t = i / tubular;
        const P = curve.getPointAt(t);
        const N = frames.normals[i], B = frames.binormals[i];
        const r = radiusAt(t);
        for (let j = 0; j <= radial; j++) {
            const a = (j / radial) * Math.PI * 2;
            const c = Math.cos(a), s = Math.sin(a);
            pos.push(P.x + r * (c * N.x + s * B.x), P.y + r * (c * N.y + s * B.y), P.z + r * (c * N.z + s * B.z));
            fold.push(foldAt ? foldAt(t, a) : 0.8);
        }
    }
    for (let i = 0; i < tubular; i++) {
        for (let j = 0; j < radial; j++) {
            const a = i * (radial + 1) + j, b = a + radial + 1;
            idx.push(a, b, a + 1, b, b + 1, a + 1);
        }
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    geo.setAttribute('aFold', new THREE.Float32BufferAttribute(fold, 1));
    geo.setIndex(idx);
    geo.computeVertexNormals();
    return geo;
}

function ellipsoidGeo(rx, ry, rz, seg = 28) {
    const geo = new THREE.SphereGeometry(1, seg, Math.round(seg * 0.7));
    geo.scale(rx, ry, rz);
    const n = geo.getAttribute('position').count;
    geo.setAttribute('aFold', new THREE.BufferAttribute(new Float32Array(n).fill(0.75), 1));
    return geo;
}

function disposeDeep(obj) {
    obj?.traverse?.((o) => {
        o.geometry?.dispose?.();
        const mats = Array.isArray(o.material) ? o.material : (o.material ? [o.material] : []);
        for (const m of mats) { m.map?.dispose?.(); m.dispose?.(); }
    });
}

// ── the public factory ───────────────────────────────────────────────────

export function createBrainVisuals() {
    /** Everything that turns with the notes (child of scene.js universeGroup). */
    const decor = new THREE.Group();
    decor.name = 'brainDecor';
    /** Everything that stays put behind the brain (child of the scene). */
    const backdrop = new THREE.Group();
    backdrop.name = 'brainBackdrop';

    const state = { cortex: 1.0, black: false, fiberColor: 'category', motion: 1.0 };
    const cortexMats = [];
    let anatomy = null, tracts = null, glia = null, motes = null, sky = null;

    // ── anatomy, built once per session (≈0.3 s — the folds are real math) ──
    function ensureAnatomy() {
        if (anatomy) return;
        anatomy = new THREE.Group();
        anatomy.name = 'anatomy';
        const shellMat = cortexMaterial({ opacity: 0.5 });
        cortexMats.push(shellMat);
        for (const side of ['L', 'R']) {
            const m = new THREE.Mesh(geometryFromShell(shellGeometry(side, 6)), shellMat);
            m.renderOrder = -2;
            m.frustumCulled = false;
            anatomy.add(m);
        }
        const cbMat = cortexMaterial({ opacity: 0.55, tint: 0xb3a6ff, sulcus: 0x1d1440 });
        cortexMats.push(cbMat);
        const cb = new THREE.Mesh(geometryFromShell(shellGeometry('CB', 5)), cbMat);
        cb.renderOrder = -2;
        anatomy.add(cb);

        const stemMat = cortexMaterial({ opacity: 0.42, tint: 0x8fb0ff, sulcus: 0x1c2250, scan: 0.6, fadeBelow: -106 });
        cortexMats.push(stemMat);
        anatomy.add(new THREE.Mesh(
            taperedTube(STEM_PATH, stemRadius, 60, 24, (t, a) => 0.55 + 0.45 * Math.abs(Math.sin(a * 6 + t * 3))),
            stemMat));

        // Deep nuclei, faint — seen through the cortex, never in front of it.
        const deepMat = cortexMaterial({ opacity: 0.08, tint: 0xc2a8ff, sulcus: 0x2a1a50, scan: 0.3, back: 0.2 });
        cortexMats.push(deepMat);
        for (const s of [1, -1]) {
            const th = new THREE.Mesh(ellipsoidGeo(9, 8, 15), deepMat);
            th.position.set(s * 11, 4, -14);
            anatomy.add(th);
            const am = new THREE.Mesh(ellipsoidGeo(6, 6, 7, 18), deepMat);
            am.position.set(s * 26, -28, 28);
            anatomy.add(am);
            const hip = new THREE.Mesh(taperedTube(
                [{ x: s * 26, y: -28, z: 22 }, { x: s * 30, y: -22, z: 2 }, { x: s * 29, y: -14, z: -22 }, { x: s * 22, y: -2, z: -36 }],
                (t) => 5.5 - 3 * t, 32, 12), deepMat);
            anatomy.add(hip);
        }
        decor.add(anatomy);

        // The brain's own white matter.
        const tr = tractPolylines();
        const S = tr.S, V = S + 1;
        const nV = tr.fibers * V;
        const colors = new Float32Array(nV * 3);
        const tAttr = new Float32Array(nV);
        const seedAttr = new Float32Array(nV);
        const index = new Uint32Array(tr.fibers * S * 2);
        const p = tr.points;
        let w = 0;
        for (let f = 0; f < tr.fibers; f++) {
            const seed = Math.random();
            for (let k = 0; k < V; k++) {
                const v = f * V + k;
                const a = Math.max(f * V, v - 1), b = Math.min(f * V + S, v + 1);
                dtiColor(p[b * 3] - p[a * 3], p[b * 3 + 1] - p[a * 3 + 1], p[b * 3 + 2] - p[a * 3 + 2], colors, v * 3);
                tAttr[v] = k / S;
                seedAttr[v] = seed;
                if (k < S) { index[w++] = v; index[w++] = v + 1; }
            }
        }
        const tg = new THREE.BufferGeometry();
        tg.setAttribute('position', new THREE.BufferAttribute(p, 3));
        tg.setAttribute('aColor', new THREE.BufferAttribute(colors, 3));
        tg.setAttribute('aT', new THREE.BufferAttribute(tAttr, 1));
        tg.setAttribute('aSeed', new THREE.BufferAttribute(seedAttr, 1));
        tg.setIndex(new THREE.BufferAttribute(index, 1));
        tracts = new THREE.LineSegments(tg, new THREE.ShaderMaterial({
            uniforms: { uTime: { value: 0 }, uOpacity: { value: 0.028 }, uMotion: { value: 1 } },
            vertexShader: tractVert, fragmentShader: tractFrag,
            transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
        }));
        tracts.frustumCulled = false;
        decor.add(tracts);

        // Backdrop: a lightbox, not a sky. Plus slow motes for depth.
        sky = new THREE.Mesh(new THREE.SphereGeometry(1700, 32, 20), new THREE.ShaderMaterial({
            vertexShader: backdropVert, fragmentShader: backdropFrag,
            side: THREE.BackSide, depthWrite: false,
        }));
        sky.renderOrder = -10;
        backdrop.add(sky);
        const M = 1400, mp = new Float32Array(M * 3), mc = new Float32Array(M * 3);
        for (let i = 0; i < M; i++) {
            const r = 220 + Math.random() * 700;
            const z = Math.random() * 2 - 1, a = Math.random() * Math.PI * 2, s = Math.sqrt(1 - z * z);
            mp[i * 3] = r * s * Math.cos(a); mp[i * 3 + 1] = r * z * 0.6; mp[i * 3 + 2] = r * s * Math.sin(a);
            const b = 0.15 + Math.random() * 0.35;
            mc[i * 3] = 0.55 * b; mc[i * 3 + 1] = 0.7 * b; mc[i * 3 + 2] = 1.0 * b;
        }
        const mg = new THREE.BufferGeometry();
        mg.setAttribute('position', new THREE.BufferAttribute(mp, 3));
        mg.setAttribute('color', new THREE.BufferAttribute(mc, 3));
        motes = new THREE.Points(mg, new THREE.PointsMaterial({
            map: dotTexture(), vertexColors: true, size: 2.4, sizeAttenuation: true,
            transparent: true, opacity: 0.7, depthWrite: false, blending: THREE.AdditiveBlending, fog: false,
        }));
        backdrop.add(motes);
        applyDecor();
    }

    // ── tissue tinted by the regions of THIS brain ──
    function mountRegions(universe) {
        ensureAnatomy();
        unmountRegions();
        const gp = gliaPoints(22000, universe.galaxies);
        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.BufferAttribute(gp.positions, 3));
        geo.setAttribute('color', new THREE.BufferAttribute(gp.colors, 3));
        glia = new THREE.Points(geo, new THREE.PointsMaterial({
            map: dotTexture(), vertexColors: true, size: 1.1, sizeAttenuation: true,
            transparent: true, opacity: 0.4, depthWrite: false, blending: THREE.AdditiveBlending, fog: false,
        }));
        glia.frustumCulled = false;
        decor.add(glia);
        applyDecor();
    }
    function unmountRegions() {
        if (!glia) return;
        decor.remove(glia);
        disposeDeep(glia);
        glia = null;
        detail.releaseAll();
    }

    // ── the vault's fibres ──
    const routePts = [0, 1, 2, 3, 4].map(() => ({ x: 0, y: 0, z: 0 }));
    const sampleBuf = new Float32Array(FIBER_V * 3);

    function writeFiber(obj, universe, i) {
        const e = universe.edges[i];
        const c = fiberRoute(universe.nodes[e.a], universe.nodes[e.b], routePts);
        sampleRoute(routePts, c, FIBER_S, sampleBuf, 0);
        const pos = obj.userData.pos;
        pos.set(sampleBuf, i * FIBER_V * 3);
        if (state.fiberColor === 'dti') {
            const col = obj.userData.col;
            for (let k = 0; k < FIBER_V; k++) {
                const a = Math.max(0, k - 1), b = Math.min(FIBER_S, k + 1);
                dtiColor(sampleBuf[b * 3] - sampleBuf[a * 3], sampleBuf[b * 3 + 1] - sampleBuf[a * 3 + 1],
                         sampleBuf[b * 3 + 2] - sampleBuf[a * 3 + 2], col, (i * FIBER_V + k) * 3);
            }
        }
    }

    function writeCategoryColors(obj, universe) {
        const col = obj.userData.col;
        const ca = new THREE.Color(), cb = new THREE.Color();
        universe.edges.forEach((e, i) => {
            ca.setHex(universe.nodes[e.a].color);
            cb.setHex(universe.nodes[e.b].color);
            for (let k = 0; k < FIBER_V; k++) {
                const t = k / FIBER_S, o = (i * FIBER_V + k) * 3;
                col[o] = ca.r + (cb.r - ca.r) * t;
                col[o + 1] = ca.g + (cb.g - ca.g) * t;
                col[o + 2] = ca.b + (cb.b - ca.b) * t;
            }
        });
    }

    /**
     * The edge object scene.js composites alpha into, exactly like the
     * universe's LineSegments — plus aT/aSpark for the action potential.
     * userData.vpe tells scene how many vertices carry one edge's alpha.
     */
    function buildFibers(universe) {
        const E = universe.edges.length;
        if (!E) return null;
        const nV = E * FIBER_V;
        const pos = new Float32Array(nV * 3);
        const col = new Float32Array(nV * 3);
        const alpha = new Float32Array(nV);
        const tArr = new Float32Array(nV);
        const spark = new Float32Array(nV * 2);
        const index = new Uint32Array(E * FIBER_S * 2);
        const baseAlpha = new Float32Array(E);
        const intra = new Uint8Array(E);
        let w = 0;
        for (let i = 0; i < E; i++) {
            const e = universe.edges[i];
            const same = universe.nodes[e.a].category === universe.nodes[e.b].category;
            // A universe spreads its edges over a sky; a brain packs the same
            // 12k links into one skull, so far more of them cross every pixel.
            // At the universe's 0.12/0.035 the frontal lobes burned to white;
            // at 0.045 they still did. Measured on the 1.7k-note vault.
            const base = same ? 0.018 : 0.007;
            baseAlpha[i] = base;
            intra[i] = same ? 1 : 0;
            for (let k = 0; k < FIBER_V; k++) {
                const v = i * FIBER_V + k;
                alpha[v] = base;
                tArr[v] = k / FIBER_S;
                spark[v * 2] = -1e5;
                spark[v * 2 + 1] = 1;
                if (k < FIBER_S) { index[w++] = v; index[w++] = v + 1; }
            }
        }
        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
        geo.setAttribute('aColor', new THREE.BufferAttribute(col, 3));
        geo.setAttribute('aAlpha', new THREE.BufferAttribute(alpha, 1));
        geo.setAttribute('aT', new THREE.BufferAttribute(tArr, 1));
        geo.setAttribute('aSpark', new THREE.BufferAttribute(spark, 2));
        geo.setIndex(new THREE.BufferAttribute(index, 1));
        const obj = new THREE.LineSegments(geo, new THREE.ShaderMaterial({
            uniforms: {
                uTime: { value: 0 }, uMotion: { value: 1 }, uStarScale: { value: 0.85 },
                uEdgeAlpha: { value: 1 }, uSpark: { value: 1 }, uSparkSpeed: { value: 1.5 },
            },
            vertexShader: fiberVert, fragmentShader: fiberFrag,
            transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
        }));
        obj.frustumCulled = false;     // positions are rewritten while settling
        obj.userData = {
            baseAlpha, intra, vpe: FIBER_V, pos, col,
            dirty: new Uint8Array(E), dirtyCount: 0, cursor: 0,
        };
        if (state.fiberColor !== 'dti') writeCategoryColors(obj, universe);
        for (let i = 0; i < E; i++) writeFiber(obj, universe, i);
        return obj;
    }

    /** Mark every fibre touching a moved neuron, then re-sample up to the
     *  frame budget. `moved` is a Uint8Array over nodes, or null for "all". */
    function updateFibers(obj, universe, moved) {
        if (!obj) return;
        const ud = obj.userData, E = universe.edges.length;
        if (moved) {
            for (let i = 0; i < E; i++) {
                if (ud.dirty[i]) continue;
                const e = universe.edges[i];
                if (moved[e.a] || moved[e.b]) { ud.dirty[i] = 1; ud.dirtyCount++; }
            }
        }
        flushFibers(obj, universe, FIBER_BUDGET);
    }

    function flushFibers(obj, universe, budget) {
        const ud = obj.userData, E = universe.edges.length;
        if (!ud.dirtyCount) return;
        let done = 0;
        for (let n = 0; n < E && done < budget; n++) {
            const i = (ud.cursor + n) % E;
            if (!ud.dirty[i]) continue;
            writeFiber(obj, universe, i);
            ud.dirty[i] = 0;
            ud.dirtyCount--;
            done++;
            if (done >= budget) { ud.cursor = (i + 1) % E; break; }
        }
        const geo = obj.geometry;
        geo.getAttribute('position').needsUpdate = true;
        if (state.fiberColor === 'dti') geo.getAttribute('aColor').needsUpdate = true;
    }

    /** Fire an action potential down fibre `i`, from end A (dir +1) or B (-1). */
    function spark(obj, i, dir, nowSec) {
        if (!obj) return;
        const attr = obj.geometry.getAttribute('aSpark');
        const base = i * FIBER_V * 2;
        for (let k = 0; k < FIBER_V; k++) {
            attr.array[base + k * 2] = nowSec;
            attr.array[base + k * 2 + 1] = dir;
        }
        attr.addUpdateRange(base, FIBER_V * 2);
        attr.needsUpdate = true;
    }

    function setFiberColor(mode, obj, universe) {
        state.fiberColor = mode === 'dti' ? 'dti' : 'category';
        if (!obj || !universe) return;
        if (state.fiberColor === 'dti') {
            for (let i = 0; i < universe.edges.length; i++) writeFiber(obj, universe, i);
        } else {
            writeCategoryColors(obj, universe);
        }
        obj.geometry.getAttribute('aColor').needsUpdate = true;
    }

    // ── neurons up close ──
    const detail = createNeuronDetail(decor);

    // ── settings ──
    function applyDecor() {
        const k = state.cortex;
        for (const m of cortexMats) {
            m.uniforms.uOpacity.value = (m.userData.base ??= m.uniforms.uOpacity.value) * k;
        }
        // Every tract converges on the callosum or the internal capsule, so
        // they stack at the brain's centre far faster than anywhere else —
        // at 0.085 that centre bloomed into a white sun.
        if (tracts) tracts.material.uniforms.uOpacity.value = 0.028 * Math.min(1.6, k);
        if (glia) glia.material.opacity = 0.4 * Math.min(1.3, 0.35 + k * 0.65);
        if (sky) sky.visible = !state.black;
        if (motes) motes.visible = !state.black;
    }
    function setCortex(v) { state.cortex = v; applyDecor(); }
    function setBlack(on) { state.black = !!on; applyDecor(); }
    function setMotion(v) { state.motion = v; if (tracts) tracts.material.uniforms.uMotion.value = v; }

    /** Per frame. ctx: { now (s), dt, camera, universeGroup, universe, pulse (Float32Array|null) } */
    function tick(ctx) {
        for (const m of cortexMats) m.uniforms.uTime.value = ctx.now;
        if (tracts) tracts.material.uniforms.uTime.value = ctx.now;
        if (motes) motes.rotation.y += ctx.dt * 0.004 * state.motion;
        detail.update(ctx);
    }

    function dispose() {
        unmountRegions();
        detail.dispose();
        disposeDeep(decor);
        disposeDeep(backdrop);
        decor.clear();
        backdrop.clear();
        anatomy = tracts = motes = sky = null;
        cortexMats.length = 0;
    }

    return {
        decor, backdrop,
        ensureAnatomy, mountRegions, unmountRegions,
        buildFibers, updateFibers, spark, setFiberColor,
        setCortex, setBlack, setMotion,
        tick, dispose,
        get fiberColor() { return state.fiberColor; },
    };
}

// ── neurons up close ─────────────────────────────────────────────────────

const DETAIL_SLOTS = 40;
/** Neurons grow their arbour inside this distance from the camera and are
 *  fully grown inside DETAIL_NEAR. */
const DETAIL_FAR = 80, DETAIL_NEAR = 38;
const DETAIL_PICK_MS = 160;

function createNeuronDetail(parent) {
    const group = new THREE.Group();
    group.name = 'neuronDetail';
    parent.add(group);

    const templates = { pyramidal: [], stellate: [], purkinje: [] };
    const makeGeo = (t) => {
        const g = new THREE.BufferGeometry();
        g.setAttribute('position', new THREE.BufferAttribute(t.pos, 3));
        g.setAttribute('aOrder', new THREE.BufferAttribute(t.order, 1));
        g.setAttribute('aAxon', new THREE.BufferAttribute(t.axon, 1));
        return g;
    };
    for (let i = 0; i < 6; i++) templates.pyramidal.push(makeGeo(neuronTemplate('pyramidal', 100 + i)));
    for (let i = 0; i < 3; i++) templates.stellate.push(makeGeo(neuronTemplate('stellate', 200 + i)));
    for (let i = 0; i < 3; i++) templates.purkinje.push(makeGeo(neuronTemplate('purkinje', 300 + i)));
    const somaPyr = new THREE.ConeGeometry(0.55, 1.3, 5);
    const somaRound = new THREE.SphereGeometry(0.5, 10, 8);

    const slots = [];
    for (let s = 0; s < DETAIL_SLOTS; s++) {
        const mat = new THREE.ShaderMaterial({
            uniforms: {
                uColor: { value: new THREE.Color() }, uOpacity: { value: 0 },
                uFire: { value: -1 }, uTime: { value: 0 },
            },
            vertexShader: dendriteVert, fragmentShader: dendriteFrag,
            transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
        });
        const lines = new THREE.LineSegments(templates.pyramidal[0], mat);
        lines.frustumCulled = false;
        const soma = new THREE.Mesh(somaPyr, new THREE.MeshBasicMaterial({
            color: 0xffffff, transparent: true, opacity: 0, depthWrite: false, blending: THREE.AdditiveBlending,
        }));
        const holder = new THREE.Group();
        holder.add(lines, soma);
        holder.visible = false;
        group.add(holder);
        slots.push({ holder, lines, soma, mat, node: -1, want: false, fade: 0, spin: 0, prevPulse: 0, fireAt: -1 });
    }

    const inv = new THREE.Matrix4();
    const camLocal = new THREE.Vector3();
    const Y = new THREE.Vector3(0, 1, 0);
    const nrm = new THREE.Vector3();
    const qa = new THREE.Quaternion(), qb = new THREE.Quaternion();
    let lastPick = 0;
    const wanted = new Set();

    function pick(universe) {
        wanted.clear();
        const nodes = universe.nodes;
        // K nearest within DETAIL_FAR — brute force over ~2k nodes, 6×/s.
        const best = [];
        const far2 = DETAIL_FAR * DETAIL_FAR;
        for (let i = 0; i < nodes.length; i++) {
            const p = nodes[i].position;
            const dx = p.x - camLocal.x, dy = p.y - camLocal.y, dz = p.z - camLocal.z;
            const d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > far2) continue;
            if (best.length < DETAIL_SLOTS) { best.push([d2, i]); if (best.length === DETAIL_SLOTS) best.sort((a, b) => b[0] - a[0]); }
            else if (d2 < best[0][0]) { best[0] = [d2, i]; best.sort((a, b) => b[0] - a[0]); }
        }
        for (const [, i] of best) wanted.add(i);
        for (const s of slots) s.want = s.node >= 0 && wanted.has(s.node);
        const taken = new Set(slots.filter(s => s.node >= 0 && s.want).map(s => s.node));
        for (const i of wanted) {
            if (taken.has(i)) continue;
            // A free slot, or one that has faded out entirely.
            const s = slots.find(x => x.node < 0) ?? slots.find(x => !x.want && x.fade < 0.02);
            if (!s) break;
            assign(s, i, nodes[i]);
            taken.add(i);
        }
    }

    function assign(s, i, n) {
        const h = hashStr(n.id);
        let kind = 'pyramidal';
        if (n.surf === 'CB') kind = 'purkinje';
        else if (n.surf === 'V') kind = 'stellate';
        const list = templates[kind];
        s.lines.geometry = list[h % list.length];
        s.soma.geometry = kind === 'pyramidal' ? somaPyr : somaRound;
        s.mat.uniforms.uColor.value.setHex(n.color);
        s.soma.material.color.setHex(n.color).lerp(new THREE.Color(0xffffff), 0.35);
        s.node = i;
        s.want = true;
        s.fade = 0;
        s.prevPulse = 0;
        s.fireAt = -1;
        s.spin = ((h >>> 8) % 628) / 100;
        s.scale = 0.62 * (0.85 + Math.min(3.5, n.size) * 0.12);
    }

    function update(ctx) {
        const { universe, camera, universeGroup } = ctx;
        if (!universe || !universe.nodes.length) { hideAll(); return; }
        universeGroup.updateMatrixWorld();
        inv.copy(universeGroup.matrixWorld).invert();
        camLocal.copy(camera.position).applyMatrix4(inv);
        const nowMs = ctx.now * 1000;
        if (nowMs - lastPick > DETAIL_PICK_MS) { lastPick = nowMs; pick(universe); }

        for (const s of slots) {
            if (s.node < 0) continue;
            const n = universe.nodes[s.node];
            if (!n) { release(s); continue; }
            const p = n.position;
            const d = Math.hypot(p.x - camLocal.x, p.y - camLocal.y, p.z - camLocal.z);
            const target = s.want ? 1 - smoothstep(DETAIL_NEAR, DETAIL_FAR, d) : 0;
            s.fade += (target - s.fade) * Math.min(1, ctx.dt * 5);
            if (!s.want && s.fade < 0.01) { release(s); continue; }
            s.holder.visible = s.fade > 0.005;
            s.holder.position.set(p.x, p.y, p.z);
            nrm.set(n.normal.x, n.normal.y, n.normal.z);
            qa.setFromUnitVectors(Y, nrm);
            qb.setFromAxisAngle(nrm, s.spin);
            s.holder.quaternion.multiplyQuaternions(qb, qa);
            s.holder.scale.setScalar(s.scale);
            const pulse = ctx.pulse ? Math.min(1.5, ctx.pulse[s.node] || 0) : 0;
            // The lightning envelope flickers, so "fired" is its rising edge,
            // not its level — otherwise every flicker would restart the wave.
            if (pulse > 0.35 && s.prevPulse <= 0.35 && (s.fireAt < 0 || ctx.now - s.fireAt > 0.9)) s.fireAt = ctx.now;
            s.prevPulse = pulse;
            s.mat.uniforms.uOpacity.value = s.fade;
            // Back to "never" once the wave has run out, rather than counting
            // up for as long as the neuron stays on screen.
            const since = ctx.now - s.fireAt;
            s.mat.uniforms.uFire.value = s.fireAt < 0 || since > 1.5 ? -1 : since;
            s.mat.uniforms.uTime.value = ctx.now;
            s.soma.material.opacity = s.fade * (0.55 + pulse * 0.4);
        }
    }

    function release(s) {
        s.node = -1; s.want = false; s.fade = 0; s.holder.visible = false;
    }
    function hideAll() { for (const s of slots) if (s.node >= 0) release(s); }

    function dispose() {
        for (const list of Object.values(templates)) list.forEach(g => g.dispose());
        somaPyr.dispose(); somaRound.dispose();
        for (const s of slots) { s.mat.dispose(); s.soma.material.dispose(); }
        parent.remove(group);
    }

    return { update, releaseAll: hideAll, dispose };
}

function smoothstep(e0, e1, x) {
    const t = Math.min(1, Math.max(0, (x - e0) / (e1 - e0)));
    return t * t * (3 - 2 * t);
}
