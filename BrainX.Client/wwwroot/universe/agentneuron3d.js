/* Agent Bus, drawn as one neuron: the Neural Brain theme's counterpart to the
 * solar system in agentbus3d.js.
 *
 * BrainX is the cell body. Every agent is a dendrite ending in a synapse, its
 * colour running up the branch toward the cell. It replaced an anatomical figure
 * on 2026-10-31: the card is wider than it is tall, so a standing body sat small
 * in the middle with its names far off on leader lines, and a body has a fixed
 * number of organs. A neuron fills the card, takes any number of agents, and is
 * what the brain behind it is made of.
 *
 * Presence is tissue: a live synapse glows and pulses, a quiet one is dim, a
 * failed engine smoulders, one that has never connected is a hollow ghost.
 * Traffic is an action potential: a request runs up the dendrite into the cell,
 * which flares as it lands, and the answer runs back in the brain's colour. A
 * relay between two agents goes up one dendrite and down the other, because that
 * is what the bus is: the brain in the middle, never a line between two agents.
 * A second Claude (local-agent mode) is a small synapse beside its host, the moon
 * of the other picture.
 *
 * Same public API as createAgentBus3D, so hud.js can swap one for the other on
 * the same canvas. Same budget too: no postprocessing, the loop parks itself when
 * the card is off-screen, every listener dies with dispose().
 */

import * as THREE from 'three';
import { AGENT_COLORS, colorOf, displayName, radialTexture } from './agentbus3d.js';

const BRAIN = AGENT_COLORS.brain;
const GHOST = 0x6b7196;
const FAILED = 0xff5a3c;
const INK = '#ece8de';

/* Where each dendrite ends, as angles on an ellipse the shape of the card.
 * Filled outward from the sides, where a name has room, and never reassigned
 * while an agent stays: a synapse that moved between polls would read as a
 * different agent. Past twelve, an inner ring. */
const ANGLES = [24, 156, -24, -156, 62, 118, -62, -118, 0, 180, 90, -90];
const RADIUS_X = 2.2, RADIUS_Y = 1.66, INNER_RING = 0.68;
const KNOWN = { claude: 0, codex: 1, cluadex: 2, gemini: 3, grok: 4, unity: 6, unreal: 7 };

const SOMA_R = 0.5;
/* World size of a name tag, matched to labelTexture's 192×48 canvas. */
const LABEL_H = 0.42, LABEL_W = LABEL_H * 192 / 48;

const GLOW = { live: 1.5, ready: 0.85, idle: 0.5, fault: 0.6, down: 0.42, off: 0.4, never: 0.4 };
const BRANCH = { live: 0.85, ready: 0.5, idle: 0.32, fault: 0.3, down: 0.24, off: 0.12, never: 0.12 };

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

function seededRandom(text) {
    let h = 2166136261;
    for (const ch of String(text)) h = Math.imul(h ^ ch.charCodeAt(0), 16777619);
    return () => {
        h = (h + 0x6d2b79f5) | 0;
        let t = Math.imul(h ^ (h >>> 15), 1 | h);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

// ── materials ──

/* Membrane: nearly clear facing the camera, bright at the rim, the way a cell
 * looks under a confocal microscope. */
const membraneVert = /* glsl */`
    varying vec3 vN;
    varying vec3 vV;
    void main() {
        vec4 mv = modelViewMatrix * vec4(position, 1.0);
        vN = normalize(normalMatrix * normal);
        vV = normalize(-mv.xyz);
        gl_Position = projectionMatrix * mv;
    }
`;
const membraneFrag = /* glsl */`
    precision highp float;
    varying vec3 vN;
    varying vec3 vV;
    uniform vec3  uColor;
    uniform float uGlow;
    uniform float uFill;
    uniform float uFlash;
    void main() {
        // Clamped by hand: a dot of two unit vectors can land a hair over 1,
        // and pow() of a negative base is NaN.
        float facing = clamp(abs(dot(normalize(vN), normalize(vV))), 0.0, 1.0);
        float rim = pow(1.0 - facing, 2.4);
        vec3 col = uColor * (0.5 + 1.1 * rim) * uGlow + vec3(1.0) * rim * uFlash * 0.6;
        gl_FragColor = vec4(col, clamp(uFill + rim * 0.9 + uFlash * 0.25, 0.0, 1.0));
    }
`;

function membrane(color, { fill = 0.16, glow = 1 } = {}) {
    return new THREE.ShaderMaterial({
        vertexShader: membraneVert, fragmentShader: membraneFrag,
        uniforms: {
            uColor: { value: new THREE.Color(color) }, uGlow: { value: glow },
            uFill: { value: fill }, uFlash: { value: 0 },
        },
        transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
    });
}

/* A dendrite: the brain's colour where it leaves the cell, the agent's at the
 * synapse, with a slow travelling sheen while the agent is live. */
const dendriteVert = /* glsl */`
    attribute float aT;
    varying float vT;
    varying vec3 vN;
    varying vec3 vV;
    void main() {
        vT = aT;
        vec4 mv = modelViewMatrix * vec4(position, 1.0);
        vN = normalize(normalMatrix * normal);
        vV = normalize(-mv.xyz);
        gl_Position = projectionMatrix * mv;
    }
`;
const dendriteFrag = /* glsl */`
    precision highp float;
    varying float vT;
    varying vec3 vN;
    varying vec3 vV;
    uniform vec3  uBase;
    uniform vec3  uColor;
    uniform float uOpacity;
    uniform float uSheen;
    uniform float uTime;
    void main() {
        float facing = clamp(abs(dot(normalize(vN), normalize(vV))), 0.0, 1.0);
        float body = 0.35 + 0.65 * facing;
        vec3 col = mix(uBase, uColor, smoothstep(0.08, 0.72, vT));
        float wave = 0.5 + 0.5 * sin(vT * 13.0 - uTime * 2.4);
        col *= 0.8 + uSheen * wave * wave * 0.7;
        gl_FragColor = vec4(col * body, uOpacity);
    }
`;

function dendriteMaterial(color, opacity) {
    return new THREE.ShaderMaterial({
        vertexShader: dendriteVert, fragmentShader: dendriteFrag,
        uniforms: {
            uBase: { value: new THREE.Color(BRAIN) }, uColor: { value: new THREE.Color(color) },
            uOpacity: { value: opacity }, uSheen: { value: 0 }, uTime: { value: 0 },
        },
        transparent: true, depthWrite: false, blending: THREE.AdditiveBlending,
    });
}

// ── geometry ──

/** A tube along `curve` (tip first), hair-thin at the tip and flaring into the cell at the far end. */
function taperedTube(curve, tipR, baseR, flare, tubular = 44, radial = 7) {
    const frames = curve.computeFrenetFrames(tubular, false);
    const pos = [], nor = [], at = [], idx = [];
    const p = new THREE.Vector3();
    for (let i = 0; i <= tubular; i++) {
        const u = i / tubular;
        curve.getPointAt(u, p);
        const r = tipR + (baseR - tipR) * Math.pow(u, 1.5) + flare * Math.exp(-(1 - u) * 13);
        const N = frames.normals[i], B = frames.binormals[i];
        for (let j = 0; j < radial; j++) {
            const a = (j / radial) * Math.PI * 2, c = Math.cos(a), s = Math.sin(a);
            const nx = c * N.x + s * B.x, ny = c * N.y + s * B.y, nz = c * N.z + s * B.z;
            pos.push(p.x + nx * r, p.y + ny * r, p.z + nz * r);
            nor.push(nx, ny, nz);
            at.push(1 - u);
        }
    }
    for (let i = 0; i < tubular; i++) {
        for (let j = 0; j < radial; j++) {
            const a = i * radial + j, b = i * radial + (j + 1) % radial;
            const c = a + radial, d = b + radial;
            idx.push(a, c, b, b, c, d);
        }
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setAttribute('normal', new THREE.Float32BufferAttribute(nor, 3));
    g.setAttribute('aT', new THREE.Float32BufferAttribute(at, 1));
    g.setIndex(idx);
    return g;
}

/** The curve of a dendrite, tip to cell, its wander seeded so an agent keeps its shape between runs. */
function dendriteCurve(tip, rng) {
    const dir = tip.clone().setZ(0).normalize();
    const side = new THREE.Vector3(-dir.y, dir.x, 0);
    const len = tip.length();
    const at = (k, wander) => dir.clone().multiplyScalar(len * k)
        .addScaledVector(side, (rng() - 0.5) * wander)
        .setZ(tip.z * k + (rng() - 0.5) * wander * 0.7);
    return new THREE.CatmullRomCurve3([tip.clone(), at(0.72, 0.5), at(0.42, 0.42), dir.clone().multiplyScalar(SOMA_R * 0.72)]);
}

/** Side twigs off a dendrite: what makes a line read as living tissue. Thin enough to be lines. */
function twigPoints(curve, rng, count, reach) {
    const pts = [];
    const p = new THREE.Vector3(), t = new THREE.Vector3();
    for (let i = 0; i < count; i++) {
        const u = 0.14 + (i + rng() * 0.7) / count * 0.62;
        curve.getPointAt(u, p);
        curve.getTangentAt(u, t);
        const sign = i % 2 ? 1 : -1;
        const out = new THREE.Vector3(-t.y * sign, t.x * sign, (rng() - 0.5) * 0.7).normalize();
        // Twigs lean back toward the tip, the way a branch grows away from the trunk.
        out.addScaledVector(t, -0.55).normalize();
        const len = reach * (0.55 + rng() * 0.6);
        const mid = p.clone().addScaledVector(out, len * 0.55);
        const end = p.clone().addScaledVector(out, len);
        pts.push(p.clone(), mid, mid.clone(), end);
        const fork = out.clone().applyAxisAngle(new THREE.Vector3(0, 0, 1), sign * 0.7);
        pts.push(mid.clone(), mid.clone().addScaledVector(fork, len * 0.5));
    }
    return pts;
}

function somaGeometry() {
    const g = new THREE.SphereGeometry(SOMA_R, 72, 54);
    const pos = g.attributes.position, v = new THREE.Vector3();
    for (let i = 0; i < pos.count; i++) {
        v.fromBufferAttribute(pos, i);
        const n = v.clone().normalize();
        const bumps = 0.06 * Math.sin(n.x * 3.1 + 1.2) * Math.sin(n.y * 2.7 + 0.4)
            + 0.035 * Math.sin(n.y * 6.3 + n.z * 5.1) + 0.02 * Math.sin(n.x * 9.1 - n.z * 7.3);
        v.multiplyScalar(1 + bumps);
        pos.setXYZ(i, v.x, v.y, v.z * 0.86);
    }
    g.computeVertexNormals();
    return g;
}

function labelTexture(text, align) {
    const w = 192, h = 48;
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    const ctx = c.getContext('2d');
    ctx.font = '600 28px "JetBrains Mono", "Cascadia Mono", Consolas, monospace';
    ctx.textBaseline = 'middle';
    ctx.textAlign = align;
    let shown = text;
    while (ctx.measureText(shown).width > w - 12 && shown.length > 2) shown = shown.slice(0, -2) + '…';
    const x = align === 'left' ? 6 : align === 'right' ? w - 6 : w / 2;
    ctx.lineWidth = 6;
    ctx.strokeStyle = 'rgba(0,0,0,0.85)';
    ctx.strokeText(shown, x, h / 2);
    ctx.fillStyle = INK;
    ctx.fillText(shown, x, h / 2);
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

export function createAgentNeuron3D(canvas) {
    if (!canvas) return null;

    const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: true });
    renderer.setClearColor(0x000000, 0);
    renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 1.75));

    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(34, 1, 0.1, 100);
    const cell = new THREE.Group();      // everything that sways together
    scene.add(cell);

    /* Same camera model as the solar system: spherical, dragged by hand, wheel
     * to zoom, double-click to go home. */
    const HOME_THETA = 0.1, HOME_PHI = 0.06;
    const view = { theta: HOME_THETA, phi: HOME_PHI, dist: 11, target: new THREE.Vector3(0, 0, 0) };
    const DIST_MIN = 2.0, DIST_MAX = 26;
    const PHI_MIN = -1.2, PHI_MAX = 1.2;
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

    // ── the cell ──
    const somaMat = membrane(BRAIN, { fill: 0.035, glow: 1.15 });
    cell.add(new THREE.Mesh(somaGeometry(), somaMat));
    /* A soft light inside, not a ball: anything with an edge in there read as an eye. */
    const nucleus = new THREE.Sprite(new THREE.SpriteMaterial({
        map: radialTexture(), color: 0xdce9ff, blending: THREE.AdditiveBlending,
        depthWrite: false, transparent: true, opacity: 0.75,
    }));
    nucleus.scale.setScalar(0.8);
    nucleus.position.set(0.05, 0.03, 0.1);
    cell.add(nucleus);
    const corona = new THREE.Sprite(new THREE.SpriteMaterial({
        map: radialTexture(), color: BRAIN, blending: THREE.AdditiveBlending,
        depthWrite: false, transparent: true, opacity: 0.22,
    }));
    corona.scale.setScalar(2.3);
    cell.add(corona);
    const nameTag = new THREE.Sprite(new THREE.SpriteMaterial({
        map: labelTexture('BrainX', 'center'), transparent: true, depthWrite: false, depthTest: false, opacity: 0.8,
    }));
    nameTag.scale.set(LABEL_W * 0.86, LABEL_H * 0.86, 1);
    nameTag.position.set(0, -SOMA_R - 0.32, 0.4);
    cell.add(nameTag);

    /* Bare dendrites, there with no agent on them: a cell with four arms is a
     * diagram, one with a full arbour is a neuron. */
    const arbourMat = dendriteMaterial(0x9cc2ff, 0.3);
    const arbour = new THREE.Group();
    const arbourTwigs = [];
    const rngArbour = seededRandom('arbour');
    for (let i = 0; i < 11; i++) {
        const a = (i / 11) * Math.PI * 2 + 0.26;
        const reach = 0.62 + rngArbour() * 0.3;
        const tip = new THREE.Vector3(Math.cos(a) * RADIUS_X * reach, Math.sin(a) * RADIUS_Y * reach, (rngArbour() - 0.5) * 1.5);
        const curve = dendriteCurve(tip, rngArbour);
        arbour.add(new THREE.Mesh(taperedTube(curve, 0.004, 0.022, 0.055, 28, 5), arbourMat));
        arbourTwigs.push(...twigPoints(curve, rngArbour, 3, 0.36));
    }
    arbour.add(new THREE.LineSegments(new THREE.BufferGeometry().setFromPoints(arbourTwigs),
        new THREE.LineBasicMaterial({ color: 0x9cc2ff, transparent: true, opacity: 0.16, depthWrite: false, blending: THREE.AdditiveBlending })));
    cell.add(arbour);

    const synapses = new Map();    // agent name → synapse
    const slotOf = new Map();      // agent name → slot number, stable across polls
    const boutonGeo = new THREE.SphereGeometry(0.13, 24, 18);
    const moonGeo = new THREE.SphereGeometry(0.075, 16, 12);
    const motes = [];
    const MOTE_MAX = 60;
    const TRAIL_LEN = 20;
    const moteTex = radialTexture();
    let raf = 0, running = false, lastT = 0, clock = 0, somaFlash = 0;

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
        view.target.set(0, 0, 0);
        userMoved = false;
        refit();
        e.stopPropagation(); e.preventDefault();
    }, { signal });

    // ── public API ──

    function setAgents(list) {
        const seen = new Set(list.map(a => a.name));
        // Leavers first, so a newcomer in the same poll can take the slot they free.
        for (const [name, o] of synapses) {
            const hostGone = o.hostName && !seen.has(o.hostName);
            if (seen.has(name) && !hostGone) continue;
            cell.remove(o.root);
            disposeDeep(o.root);
            synapses.delete(name);
            slotOf.delete(name);
        }
        const ordered = [...list.filter(a => !a.moonOf), ...list.filter(a => a.moonOf)];
        for (const a of ordered) {
            let o = synapses.get(a.name);
            if (!o) {
                const host = a.moonOf ? synapses.get(a.moonOf) : null;
                o = host ? buildMoon(a.name, a.moonOf, host) : buildSynapse(a.name);
                synapses.set(a.name, o);
            }
            applyPresence(o, a);
        }
    }

    function fireTraffic(name, inbound = true, forceColor = null) {
        const o = synapses.get(name);
        if (!o || motes.length >= MOTE_MAX) return;
        const color = forceColor ?? (inbound ? colorOf(name) : BRAIN);
        const sprite = new THREE.Sprite(new THREE.SpriteMaterial({
            map: moteTex, color, blending: THREE.AdditiveBlending, transparent: true, depthWrite: false,
        }));
        sprite.scale.setScalar(0.3);
        cell.add(sprite);
        const trailGeo = new THREE.BufferGeometry();
        trailGeo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(TRAIL_LEN * 3), 3));
        trailGeo.setAttribute('color', new THREE.BufferAttribute(new Float32Array(TRAIL_LEN * 3), 3));
        const trail = new THREE.Line(trailGeo, new THREE.LineBasicMaterial({
            vertexColors: true, blending: THREE.AdditiveBlending, transparent: true, depthWrite: false,
        }));
        trail.frustumCulled = false;
        cell.add(trail);
        motes.push({ sprite, trail, synapse: o, t: 0, dur: o.dur, inbound, head: new THREE.Color(color), history: [] });
    }

    /** Up one dendrite into the cell, then down the other: see the file header. */
    function fireRelay(fromName, toName) {
        const src = synapses.get(fromName), dst = synapses.get(toName);
        if (!src && !dst) return;
        const legs = (src ? 1 : 0) + (dst ? 1 : 0);
        if (motes.length + legs > MOTE_MAX) return;
        const color = colorOf(src ? fromName : toName);
        if (src) fireTraffic(fromName, true, color);
        if (!dst) return;
        const handoff = src ? src.dur * 1000 : 0;
        setTimeout(() => { if (running) fireTraffic(toName, false, color); }, handoff);
    }

    /* Ellipse and names both in frame, whichever way the card is cut. */
    function fitDist() {
        const t = Math.tan(camera.fov * Math.PI / 360);
        const aspect = Math.max(0.2, camera.aspect);
        const vertical = (RADIUS_Y + 0.5) / t;
        const horizontal = (RADIUS_X + LABEL_W * 0.95) / (t * aspect);
        fitted = clamp(Math.max(vertical, horizontal), DIST_MIN, DIST_MAX);
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
        motes.forEach(m => { disposeDeep(m.sprite); disposeDeep(m.trail); });
        motes.length = 0;
        boutonGeo.dispose();
        moonGeo.dispose();
        moteTex.dispose();
        disposeDeep(scene);
        renderer.dispose();
    }

    // ── internals ──

    function nextSlot(name) {
        if (slotOf.has(name)) return slotOf.get(name);
        const taken = new Set(slotOf.values());
        let slot = KNOWN[String(name).toLowerCase()];
        if (slot == null || taken.has(slot)) {
            slot = 0;
            while (taken.has(slot)) slot++;
        }
        slotOf.set(name, slot);
        return slot;
    }

    function tipOf(slot, rng) {
        const ring = Math.floor(slot / ANGLES.length);
        const scale = ring === 0 ? 1 : INNER_RING;
        const a = (ANGLES[slot % ANGLES.length] + (ring ? 15 : 0)) * Math.PI / 180;
        return new THREE.Vector3(Math.cos(a) * RADIUS_X * scale, Math.sin(a) * RADIUS_Y * scale, (rng() - 0.5) * 0.3);
    }

    function addLabel(root, name, at, size = 1) {
        const right = at.x >= 0;
        const label = new THREE.Sprite(new THREE.SpriteMaterial({
            map: labelTexture(displayName(name), right ? 'left' : 'right'),
            transparent: true, depthWrite: false, depthTest: false,
        }));
        label.scale.set(LABEL_W * size, LABEL_H * size, 1);
        label.position.copy(at).add(new THREE.Vector3((LABEL_W * size / 2 + 0.16) * (right ? 1 : -1), 0, 0));
        root.add(label);
        return label;
    }

    function buildSynapse(name) {
        const rng = seededRandom(name);
        const color = colorOf(name);
        const tip = tipOf(nextSlot(name), rng);
        const curve = dendriteCurve(tip, rng);
        const root = new THREE.Group();

        const branch = dendriteMaterial(color, 0.5);
        root.add(new THREE.Mesh(taperedTube(curve, 0.016, 0.05, 0.11), branch));
        const twigs = new THREE.LineSegments(new THREE.BufferGeometry().setFromPoints(twigPoints(curve, rng, 5, 0.42)),
            new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.25, depthWrite: false, blending: THREE.AdditiveBlending }));
        root.add(twigs);

        const mat = membrane(color, { fill: 0.3, glow: 1.2 });
        const bouton = new THREE.Mesh(boutonGeo, mat);
        bouton.position.copy(tip);
        root.add(bouton);
        const halo = new THREE.Sprite(new THREE.SpriteMaterial({
            map: radialTexture(), color, blending: THREE.AdditiveBlending, depthWrite: false, transparent: true, opacity: 0.5,
        }));
        halo.scale.setScalar(0.85);
        halo.position.copy(tip);
        root.add(halo);
        const label = addLabel(root, name, tip);
        cell.add(root);
        return { root, curve, pos: tip, bouton, mat, halo, branch, twigs, label, color, flash: 0,
            dur: clamp(curve.getLength() * 0.42, 0.7, 1.4), phase: rng() * 6.28 };
    }

    function buildMoon(name, hostName, host) {
        const color = colorOf(name);
        const away = host.pos.clone().setZ(0).normalize();
        const pos = host.pos.clone().addScaledVector(away, 0.1).add(new THREE.Vector3(0, host.pos.y >= 0 ? 0.3 : -0.3, 0.1));
        const root = new THREE.Group();
        const mat = membrane(color, { fill: 0.3, glow: 1.2 });
        const bouton = new THREE.Mesh(moonGeo, mat);
        bouton.position.copy(pos);
        root.add(bouton);
        const stalk = new THREE.Line(new THREE.BufferGeometry().setFromPoints([pos, host.pos]),
            new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.4, depthWrite: false, blending: THREE.AdditiveBlending }));
        root.add(stalk);
        const label = addLabel(root, name, pos, 0.84);
        cell.add(root);
        return { root, pos, bouton, mat, label, color, isMoon: true, host, hostName, flash: 0, dur: host.dur + 0.15, phase: 0, twigs: null, branch: null, halo: null };
    }

    function applyPresence(o, a) {
        const state = a.state || (a.online ? 'live' : (a.everSeen !== false ? 'idle' : 'never'));
        const ghost = state === 'never' || state === 'off';
        const failed = state === 'fault' || state === 'down';
        o.state = state;
        o.online = state === 'live';
        o.everSeen = a.everSeen !== false;
        o.ghost = ghost;
        const c = new THREE.Color(ghost ? GHOST : o.color);
        if (failed) c.lerp(new THREE.Color(FAILED), 0.65);
        o.mat.uniforms.uColor.value.copy(c);
        o.mat.uniforms.uFill.value = ghost ? 0.02 : o.online ? 0.42 : 0.2;
        o.baseGlow = GLOW[state] ?? 0.4;
        if (o.branch) {
            o.branch.uniforms.uColor.value.copy(c);
            o.branch.uniforms.uOpacity.value = BRANCH[state] ?? 0.2;
            o.branch.uniforms.uSheen.value = o.online ? 1 : 0;
            o.twigs.material.color.copy(c);
            o.twigs.material.opacity = (BRANCH[state] ?? 0.2) * 0.42;
            o.halo.material.color.copy(c);
            o.halo.material.opacity = ghost ? 0 : o.online ? 0.55 : 0.2;
        }
        o.label.material.opacity = ghost ? 0.5 : o.online ? 1 : 0.76;
    }

    function updateTrail(m, fade) {
        m.history.unshift(m.sprite.position.clone());
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
    /** Where an impulse is at progress k (0 = at the synapse, 1 = in the cell). */
    function pathPoint(o, k, out) {
        // Curve.getPointAt outside [0, 1] indexes past its arc-length table and
        // throws, which inside the render loop is the loop.
        k = clamp(k, 0, 1);
        if (!o.isMoon) return o.curve.getPointAt(k, out);
        if (k < 0.16) return out.lerpVectors(o.pos, o.host.pos, k / 0.16);
        return o.host.curve.getPointAt((k - 0.16) / 0.84, out);
    }

    function tick(now) {
        if (!running) return;
        // Scheduled FIRST: if anything below ever throws, the card keeps
        // animating instead of freezing with `running` still claiming life.
        raf = requestAnimationFrame(tick);
        // A rAF timestamp is the frame's START and the first after start() can
        // predate performance.now() taken there. Negative dt ran impulses
        // backwards off the start of their path.
        const dt = clamp((now - lastT) / 1000, 0, 0.05);
        lastT = now;
        clock += dt;

        // A slow turn: a still cell in a live panel reads as a frozen one.
        cell.rotation.y = Math.sin(clock * 0.2) * 0.2;
        cell.rotation.x = Math.sin(clock * 0.13) * 0.05;

        const breath = 1 + Math.sin(clock * 1.1) * 0.016;
        somaFlash = Math.max(0, somaFlash - dt * 2.6);
        somaMat.uniforms.uFlash.value = somaFlash;
        somaMat.uniforms.uGlow.value = 1.15 + somaFlash * 0.7;
        nucleus.material.opacity = 0.7 + Math.sin(clock * 1.1) * 0.08 + somaFlash * 0.3;
        nucleus.scale.setScalar(0.8 * breath);
        corona.material.opacity = 0.2 + Math.sin(clock * 1.1) * 0.03 + somaFlash * 0.28;
        arbourMat.uniforms.uTime.value = clock;

        for (const o of synapses.values()) {
            o.flash = Math.max(0, o.flash - dt * 3);
            const pulse = o.online ? 1 + 0.2 * Math.sin(clock * 2.1 + o.phase) : 1;
            o.mat.uniforms.uGlow.value = o.baseGlow * pulse + o.flash;
            o.mat.uniforms.uFlash.value = o.flash;
            o.bouton.scale.setScalar(1 + o.flash * 0.35 + (o.online ? (pulse - 1) * 0.2 : 0));
            if (o.branch) o.branch.uniforms.uTime.value = clock + o.phase;
        }

        for (let i = motes.length - 1; i >= 0; i--) {
            const m = motes[i];
            m.t += dt / m.dur;
            if (m.t >= 1) {
                if (m.inbound) somaFlash = 1;
                else m.synapse.flash = 1;
                cell.remove(m.sprite, m.trail);
                disposeDeep(m.sprite); disposeDeep(m.trail);
                motes.splice(i, 1);
                continue;
            }
            pathPoint(m.synapse, m.inbound ? m.t : 1 - m.t, _p);
            // Either side of the branch, so a request and its answer passing
            // never merge into one blob.
            _p.y += m.inbound ? 0.018 : -0.018;
            m.sprite.position.copy(_p);
            const fade = m.t < 0.1 ? m.t / 0.1 : (m.t > 0.92 ? (1 - m.t) / 0.08 : 1);
            m.sprite.material.opacity = fade;
            updateTrail(m, fade);
        }

        renderer.render(scene, camera);
    }

    resize();
    start();

    /** Live numbers for tests and consoles: pixels cannot be read back from a
     *  composited WebGL canvas, positions and states can. */
    function debugState() {
        return {
            running,
            kind: 'neuron',
            view: { theta: +view.theta.toFixed(3), phi: +view.phi.toFixed(3), dist: +view.dist.toFixed(2), userMoved },
            planets: [...synapses.entries()].map(([name, o]) => ({
                name, slot: slotOf.get(name), state: o.state, online: o.online,
                moonOf: o.hostName ?? null, glow: +o.mat.uniforms.uGlow.value.toFixed(2), ghost: !!o.ghost,
                x: +o.pos.x.toFixed(3), y: +o.pos.y.toFixed(3),
            })),
            motes: motes.length,
        };
    }

    return { setAgents, fireTraffic, fireRelay, resize, start, stop, dispose, debugState };
}
