// ObsidianX Universe — three.js scene.
//
// Owns the canvas, camera, render loop, and all GPU resources. Exposes
// mount(brain, viewport) to build/replace the universe from a payload, and
// dispose() to tear it down. The DOM-side overlay (info card, legend) is
// driven via callbacks set by app.js so this module stays UI-agnostic.

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { forceSimulation, forceManyBody, forceLink, forceCenter, forceCollide } from 'd3-force';
import { buildUniverse } from './layout.js';

// ── shaders ────────────────────────────────────────────────────────────
// Star sprites: billboarded quads with a radial gradient so each "star"
// glows. Twinkle phase is per-star and varies subtly — no twinkle storm.
const starVert = /* glsl */`
    attribute float aSize;
    attribute float aBrightness;
    attribute float aPhase;
    attribute vec3  aColor;
    attribute float aPulse;    // 0..1, decays with time after a touch

    uniform float uTime;
    uniform float uPixelRatio;
    uniform float uSelectedIndex;
    uniform float uHoverIndex;
    uniform float uMotion;
    uniform float uSizeScale;

    varying vec3  vColor;
    varying float vBrightness;
    varying float vSelected;
    varying float vPulse;

    void main() {
        vec4 mv = modelViewMatrix * vec4(position, 1.0);
        gl_Position = projectionMatrix * mv;

        // distance-attenuated point size; gl_PointSize is in pixels.
        // Twinkle amp scales with motion slider so a dead-still universe still
        // looks intentional (and 0 = literally frozen frame).
        float twinkleAmp = 0.30 * uMotion;
        float twinkle = (1.0 - twinkleAmp) + twinkleAmp * sin(uTime * 1.4 + aPhase * 6.2831);
        float size = aSize * twinkle * (320.0 / -mv.z) * uPixelRatio * uSizeScale;

        float fIndex = float(gl_VertexID);
        float isSel = step(abs(fIndex - uSelectedIndex), 0.5);
        float isHov = step(abs(fIndex - uHoverIndex), 0.5);
        // Pulse: balloon the star up to ~3.5× during peak. Quick attack
        // (within the first ~0.15 s of life) handled on the CPU; this just
        // reads the current amplitude.
        size *= 1.0 + isSel * 1.8 + isHov * 0.6 + aPulse * 2.5;

        gl_PointSize = clamp(size, 1.0, 96.0);
        vColor = aColor;
        vBrightness = aBrightness * (1.0 + isSel * 0.6 + isHov * 0.3 + aPulse * 1.6);
        vSelected = max(isSel, isHov * 0.6);
        vPulse = aPulse;
    }
`;

const starFrag = /* glsl */`
    precision highp float;
    varying vec3  vColor;
    varying float vBrightness;
    varying float vSelected;
    varying float vPulse;
    uniform float uStarScale;

    void main() {
        vec2 uv = gl_PointCoord - vec2(0.5);
        float d = length(uv);
        if (d > 0.5) discard;

        // soft radial falloff: bright core, smooth halo, hard edge clipped.
        float core = smoothstep(0.5, 0.0, d);              // 0..1 outside→in
        float halo = smoothstep(0.5, 0.18, d) * 0.55;
        float a = (core * core) + halo;

        // selection ring: a slim bright annulus near the rim.
        float ring = smoothstep(0.42, 0.46, d) - smoothstep(0.46, 0.5, d);
        // Pulse halo: a wider outer ring that explodes outward during the
        // peak of the flash. Adds a vivid corona that bloom amplifies.
        // Lightning tint: pure white at low amplitudes, cool blue-white at
        // peaks (>1.0 overshoot allowed by the envelope) — makes the eye
        // read the flash as electrical rather than a generic glow.
        float pulseHalo = smoothstep(0.5, 0.05, d) * vPulse;
        vec3 lightningCol = mix(vec3(1.0, 1.0, 1.0),
                                vec3(0.82, 0.92, 1.30),
                                smoothstep(0.45, 1.15, vPulse));
        vec3 col = vColor * (0.6 + 0.7 * vBrightness)
                 + ring * vSelected * vec3(1.0)
                 + pulseHalo * lightningCol * 0.95;

        // uStarScale (slider) attenuates BOTH color brightness and alpha so
        // dimming visibly tames the bloom feed, not just the inner core.
        float alpha = a * (0.55 + vBrightness * 0.55 + vPulse * 0.55) * uStarScale;
        gl_FragColor = vec4(col * uStarScale, alpha);
    }
`;

// Edge shader — additive blend, faint by default, brighter near focus.
// Subtle global breathing (uTime + uMotion) keeps lines from feeling dead.
const edgeVert = /* glsl */`
    attribute vec3 aColor;
    attribute float aAlpha;
    varying vec3  vColor;
    varying float vAlpha;
    void main() {
        vColor = aColor;
        vAlpha = aAlpha;
        gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
    }
`;
const edgeFrag = /* glsl */`
    precision highp float;
    varying vec3  vColor;
    varying float vAlpha;
    uniform float uTime;
    uniform float uMotion;
    uniform float uStarScale;
    uniform float uEdgeAlpha;
    void main() {
        float breathe = 1.0 + uMotion * 0.18 * sin(uTime * 0.55);
        gl_FragColor = vec4(vColor * uStarScale, vAlpha * breathe * uStarScale * uEdgeAlpha);
    }
`;

// Background starfield: tiny dim points on a huge sphere, no animation.
function buildStarfield() {
    const COUNT = 2400;
    const positions = new Float32Array(COUNT * 3);
    for (let i = 0; i < COUNT; i++) {
        // uniformly on a sphere of radius 1200
        const u = Math.random(), v = Math.random();
        const theta = 2 * Math.PI * u;
        const phi = Math.acos(2 * v - 1);
        const r = 1100 + Math.random() * 200;
        positions[3 * i + 0] = r * Math.sin(phi) * Math.cos(theta);
        positions[3 * i + 1] = r * Math.sin(phi) * Math.sin(theta);
        positions[3 * i + 2] = r * Math.cos(phi);
    }
    const geom = new THREE.BufferGeometry();
    geom.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    const mat = new THREE.PointsMaterial({
        color: 0xb8c0ff,
        size: 0.7,
        sizeAttenuation: false,
        transparent: true,
        opacity: 0.45,
        depthWrite: false,
        blending: THREE.AdditiveBlending
    });
    return new THREE.Points(geom, mat);
}

// Nebula haze: a translucent additive sprite per galaxy, gives each region
// a soft luminous cloud that bloom amplifies into "dust".
function buildNebulaSprites(galaxies) {
    // generate a soft radial gradient texture once, share across sprites.
    const SIZE = 256;
    const c = document.createElement('canvas');
    c.width = c.height = SIZE;
    const ctx = c.getContext('2d');
    const grad = ctx.createRadialGradient(SIZE / 2, SIZE / 2, 0, SIZE / 2, SIZE / 2, SIZE / 2);
    grad.addColorStop(0.0, 'rgba(255,255,255,0.85)');
    grad.addColorStop(0.35, 'rgba(255,255,255,0.30)');
    grad.addColorStop(1.0, 'rgba(255,255,255,0)');
    ctx.fillStyle = grad;
    ctx.fillRect(0, 0, SIZE, SIZE);
    const tex = new THREE.CanvasTexture(c);
    tex.colorSpace = THREE.SRGBColorSpace;

    const group = new THREE.Group();
    for (const g of galaxies) {
        const mat = new THREE.SpriteMaterial({
            map: tex,
            color: g.color,
            transparent: true,
            opacity: 0.22,
            depthWrite: false,
            blending: THREE.AdditiveBlending
        });
        const sprite = new THREE.Sprite(mat);
        sprite.position.set(g.center.x, g.center.y, g.center.z);
        const s = g.radius * 4.5;
        sprite.scale.set(s, s, s);
        sprite.userData.galaxy = g.category;
        group.add(sprite);
    }
    return group;
}

function buildStars(nodes) {
    const COUNT = nodes.length;
    const positions = new Float32Array(COUNT * 3);
    const colors    = new Float32Array(COUNT * 3);
    const sizes     = new Float32Array(COUNT);
    const brights   = new Float32Array(COUNT);
    const phases    = new Float32Array(COUNT);
    const pulses    = new Float32Array(COUNT);

    const color = new THREE.Color();
    for (let i = 0; i < COUNT; i++) {
        const n = nodes[i];
        positions[3 * i + 0] = n.position.x;
        positions[3 * i + 1] = n.position.y;
        positions[3 * i + 2] = n.position.z;

        color.setHex(n.color);
        colors[3 * i + 0] = color.r;
        colors[3 * i + 1] = color.g;
        colors[3 * i + 2] = color.b;

        sizes[i]   = n.size;
        brights[i] = n.brightness;
        phases[i]  = Math.random();
        pulses[i]  = 0;
    }

    const geom = new THREE.BufferGeometry();
    geom.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    geom.setAttribute('aColor', new THREE.BufferAttribute(colors, 3));
    geom.setAttribute('aSize', new THREE.BufferAttribute(sizes, 1));
    geom.setAttribute('aBrightness', new THREE.BufferAttribute(brights, 1));
    geom.setAttribute('aPhase', new THREE.BufferAttribute(phases, 1));
    geom.setAttribute('aPulse', new THREE.BufferAttribute(pulses, 1));

    const mat = new THREE.ShaderMaterial({
        uniforms: {
            uTime: { value: 0 },
            uPixelRatio: { value: window.devicePixelRatio || 1 },
            uSelectedIndex: { value: -1 },
            uHoverIndex: { value: -1 },
            uMotion: { value: 1.0 },
            uStarScale: { value: 0.85 },
            uSizeScale: { value: 1.0 }
        },
        vertexShader: starVert,
        fragmentShader: starFrag,
        transparent: true,
        depthWrite: false,
        blending: THREE.AdditiveBlending
    });

    return new THREE.Points(geom, mat);
}

function buildEdges(nodes, edges) {
    if (!edges.length) return null;

    const positions = new Float32Array(edges.length * 6);
    const colors    = new Float32Array(edges.length * 6);
    const alphas    = new Float32Array(edges.length * 2);
    // Sibling arrays used by the alpha pipeline (not uploaded to GPU).
    const baseAlpha = new Float32Array(edges.length);
    const intra     = new Uint8Array(edges.length);

    const colA = new THREE.Color(), colB = new THREE.Color();
    for (let i = 0; i < edges.length; i++) {
        const a = nodes[edges[i].a];
        const b = nodes[edges[i].b];
        positions[6 * i + 0] = a.position.x;
        positions[6 * i + 1] = a.position.y;
        positions[6 * i + 2] = a.position.z;
        positions[6 * i + 3] = b.position.x;
        positions[6 * i + 4] = b.position.y;
        positions[6 * i + 5] = b.position.z;

        colA.setHex(a.color);
        colB.setHex(b.color);
        colors[6 * i + 0] = colA.r; colors[6 * i + 1] = colA.g; colors[6 * i + 2] = colA.b;
        colors[6 * i + 3] = colB.r; colors[6 * i + 4] = colB.g; colors[6 * i + 5] = colB.b;

        // intra-galaxy edges are slightly brighter than cross-galaxy ones —
        // the eye should pick up local clusters first.
        const same = a.category === b.category;
        const base = same ? 0.18 : 0.07;
        alphas[2 * i + 0] = base;
        alphas[2 * i + 1] = base;
        baseAlpha[i] = base;
        intra[i] = same ? 1 : 0;
    }

    const geom = new THREE.BufferGeometry();
    geom.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    geom.setAttribute('aColor', new THREE.BufferAttribute(colors, 3));
    geom.setAttribute('aAlpha', new THREE.BufferAttribute(alphas, 1));

    const mat = new THREE.ShaderMaterial({
        uniforms: {
            uTime: { value: 0 },
            uMotion: { value: 1.0 },
            uStarScale: { value: 0.85 },
            uEdgeAlpha: { value: 1.0 }
        },
        vertexShader: edgeVert,
        fragmentShader: edgeFrag,
        transparent: true,
        depthWrite: false,
        blending: THREE.AdditiveBlending
    });
    const obj = new THREE.LineSegments(geom, mat);
    // Stash the sibling arrays on the object so scene.js can read them
    // without re-deriving "is this edge intra-galaxy?" later.
    obj.userData.baseAlpha = baseAlpha;
    obj.userData.intra = intra;
    return obj;
}

// ── public API ─────────────────────────────────────────────────────────
export function createScene(canvas, callbacks = {}) {
    const renderer = new THREE.WebGLRenderer({
        canvas,
        antialias: true,
        alpha: false,
        powerPreference: 'high-performance'
    });
    renderer.setClearColor(0x02030a, 1);
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    renderer.setSize(canvas.clientWidth || window.innerWidth, canvas.clientHeight || window.innerHeight, false);

    const scene = new THREE.Scene();
    scene.fog = new THREE.FogExp2(0x02030a, 0.0025);

    const camera = new THREE.PerspectiveCamera(55, window.innerWidth / window.innerHeight, 0.1, 4000);
    camera.position.set(0, 60, 240);

    const controls = new OrbitControls(camera, canvas);
    controls.enableDamping = true;
    controls.dampingFactor = 0.06;
    controls.rotateSpeed = 0.6;
    controls.zoomSpeed = 0.8;
    controls.panSpeed = 0.7;
    controls.minDistance = 6;
    controls.maxDistance = 700;
    // Built-in auto-rotate (used by orbit camera mode). Disabled by default;
    // setCameraMode('orbit') flips this on. OrbitControls handles user-input
    // pauses for free — no manual idle timer needed.
    controls.autoRotate = false;
    controls.autoRotateSpeed = 0.45;   // 45 ≈ 80-sec rotation

    // postprocessing — RenderPass → bloom → output. Defaults tuned conservatively
    // so the universe reads as "luminous" not "blown out"; the settings panel
    // exposes strength so users can crank it back up if they prefer.
    const composer = new EffectComposer(renderer);
    composer.addPass(new RenderPass(scene, camera));
    const bloom = new UnrealBloomPass(
        new THREE.Vector2(window.innerWidth, window.innerHeight),
        0.55,   // strength  (was 0.95 — softened on user feedback)
        0.55,   // radius
        0.32    // threshold (was 0.18 — higher = only the brightest cores bloom)
    );
    composer.addPass(bloom);
    composer.addPass(new OutputPass());

    // background starfield is built once and never replaced. Reference is
    // kept so setBackground('black') can hide it for the "deep void" look.
    const starfieldObj = buildStarfield();
    scene.add(starfieldObj);

    // All "live" content (stars, edges, nebula) lives inside one universeGroup
    // so a single rotation pumps motion through everything coherently. Nebula
    // gets a child group that spins at a different rate for parallax.
    const universeGroup = new THREE.Group();
    const nebulaGroup = new THREE.Group();
    universeGroup.add(nebulaGroup);
    scene.add(universeGroup);

    // motion + brightness settings — exposed via setters, persisted by app.js.
    const settings = {
        motion: 1.0,        // 0 = freeze frame, 1 = default lively, 2 = brisk
        glow: 0.55,         // mirrors bloom.strength
        stars: 0.85,        // uStarScale — color/alpha intensity (renamed "Brightness" in UI)
        size:  1.0,         // uSizeScale — star size multiplier
        edges: 1.0,         // uEdgeAlpha — edge alpha multiplier
        drift: 0.0,         // 0 = freeze after settle; >0 = keep sims simmering forever
        lightning: 1.0,         // 0 = disable pulses, 1 = default lightning, 2 = blinding
        lightningSpeed: 1.0,    // 0.5 = slow majestic strike, 1 = default, 2 = frantic flicker
        cameraMode: 'free', // 'free' | 'orbit' | 'follow'
        background: 'nebula', // 'nebula' | 'black' — controls clearColor + nebula sprites + starfield
        lockSelected: true    // when a star is selected, keep it at screen centre
    };

    // Camera modes:
    //   • free   — pure OrbitControls (default)
    //   • orbit  — controls.autoRotate=true (OrbitControls handles input-pause)
    //   • follow — firePulse calls focusNode so camera flies to the touched star
    // Track-selected (separate from mode): when a node is selected, every
    // frame we lerp controls.target to the node's *current* world position
    // so it stays pinned at screen center even as physics + group rotation
    // move the star around.
    const TRACK_LERP = 0.18;       // 0..1, higher = stickier (less smooth)

    // raycaster lives across rebuilds so we don't reallocate per frame.
    const raycaster = new THREE.Raycaster();
    // Tune the picking radius for Points — without this it's basically zero.
    raycaster.params.Points.threshold = 1.8;
    const ndc = new THREE.Vector2();
    const lastPointer = { x: 0, y: 0, valid: false };

    // mutable scene state per brain mount
    let universe = null;     // { nodes, edges, galaxies }
    let starsObj = null;
    let edgesObj = null;
    let nebulaObj = null;
    let hoverIndex = -1;
    let selectedIndex = -1;
    let edgeAlphaAttr = null;   // direct reference for fast updates
    let starPosAttr = null;     // direct reference to upload settled positions
    let edgePosAttr = null;
    // Per-galaxy d3-force simulations. Each runs in disk-local 2D (u, v);
    // we project back to world after every tick. Settles in ~3 s, then
    // sims stop being touched so steady-state is zero-cost.
    let sims = [];

    // MCP pulse state: when C# forwards a node-touch event, we record the
    // start time and each frame compute the amplitude from a "lightning
    // envelope" (sum of gaussian flashes). The result is multiple bright
    // flickers over ~720 ms before going dark — like a real lightning
    // bolt rather than a smooth fade. activePulses maps starIdx → t0_ms.
    let pulseAttr = null;
    const LIGHTNING_STAR_DURATION_MS = 720;
    const LIGHTNING_EDGE_DURATION_MS = 520;
    let idToIndex = null;
    let activePulses = new Map();   // starIdx → t0_ms (performance.now())

    // Edge alpha pipeline (fixes B1 + B2 from the review).
    //
    // Three independent inputs drive each edge's rendered alpha:
    //   1. Base alpha   — set once at buildEdges (intra=0.18, cross=0.07).
    //                     Frozen. Restored after any transient effect fades.
    //   2. Selection    — when focusNode sets selectedIndex, connected
    //                     edges go to 0.85 and unconnected dim to base × 0.35.
    //   3. Pulse boost  — per-edge decaying amplitude (0..1) bumped to 1
    //                     when an arc fires on that edge from firePulse().
    //
    // Each frame we composite: final = max(selection_modulated, base + pulseBoost × 0.8).
    // Pulse boosts decay exponentially so edges return to selection/base
    // automatically — no "arc died and forgot to restore" bug.
    let edgeBaseAlpha = null;       // Float32Array length E
    let edgeIntra = null;           // Uint8Array length E — 1 if intra-galaxy
    let pulseEdgeBoost = null;      // Float32Array length E (current envelope value)
    const activeEdgeBoosts = new Map();    // edgeIdx → t0_ms (lightning envelope start)
    const MAX_ARCS_PER_PULSE = 8;

    // ── Lightning envelope ─────────────────────────────────────────────
    // Sum of gaussian flashes at staggered offsets, producing the
    // characteristic "FLASH … flicker … flicker … fade" pattern that
    // makes the eye read it as lightning rather than a smooth glow.
    // Returns 0 outside the active window so the caller can detach.
    function lightningAmpStar(rawElapsedMs) {
        const intensity = settings.lightning;
        if (intensity <= 0) return 0;
        // Speed warps the time axis: speed=2 fits the same flicker into
        // half the duration, speed=0.5 stretches it to 2× longer.
        const t = rawElapsedMs * settings.lightningSpeed;
        if (t < 0 || t >= LIGHTNING_STAR_DURATION_MS) return 0;
        // Each flash: amplitude × exp(-(t - centre)^2 / (2σ²))
        let a = 0;
        a += 1.00 * Math.exp(-((t -   0) * (t -   0)) / (2 *  30 *  30));   // initial blinding flash
        a += 0.65 * Math.exp(-((t -  90) * (t -  90)) / (2 *  25 *  25));   // first flicker
        a += 0.55 * Math.exp(-((t - 220) * (t - 220)) / (2 *  35 *  35));   // second flicker
        a += 0.30 * Math.exp(-((t - 410) * (t - 410)) / (2 *  50 *  50));   // afterglow
        // Tiny crackle so even the smooth shoulders feel chaotic.
        a *= 1.0 + Math.sin(t * 0.21) * 0.07;
        // Allow brief overshoot above 1.0 — the shader bloom turns this
        // into a white-out at the peak, which sells the lightning feel.
        // Cap the post-intensity result so intensity=2 doesn't pin alpha forever.
        return Math.max(0, Math.min(2.0, Math.min(1.5, a) * intensity));
    }

    function lightningAmpEdge(rawElapsedMs) {
        const intensity = settings.lightning;
        if (intensity <= 0) return 0;
        const t = rawElapsedMs * settings.lightningSpeed;
        if (t < 0 || t >= LIGHTNING_EDGE_DURATION_MS) return 0;
        // Edges fire ~20 ms behind the star and decay slightly faster —
        // the bolt visibly travels outward from the star core.
        let a = 0;
        a += 1.00 * Math.exp(-((t -  20) * (t -  20)) / (2 *  25 *  25));
        a += 0.55 * Math.exp(-((t - 120) * (t - 120)) / (2 *  28 *  28));
        a += 0.40 * Math.exp(-((t - 280) * (t - 280)) / (2 *  40 *  40));
        return Math.max(0, Math.min(1.8, Math.min(1.4, a) * intensity));
    }

    // Diagnostics: rate-limited warn on pulse misses so a stale id stream
    // is visible during debug instead of failing silently.
    let _missCount = 0;
    let _lastMissLogAt = 0;

    function mount(brain) {
        dispose();
        universe = buildUniverse(brain);
        if (!universe.nodes.length) {
            callbacks.onGalaxies?.([]);
            return universe;
        }

        nebulaObj = buildNebulaSprites(universe.galaxies);
        nebulaGroup.add(nebulaObj);

        edgesObj = buildEdges(universe.nodes, universe.edges);
        if (edgesObj) {
            universeGroup.add(edgesObj);
            edgeAlphaAttr = edgesObj.geometry.getAttribute('aAlpha');
            edgePosAttr = edgesObj.geometry.getAttribute('position');
            edgeBaseAlpha = edgesObj.userData.baseAlpha;
            edgeIntra = edgesObj.userData.intra;
            pulseEdgeBoost = new Float32Array(universe.edges.length);
            activeEdgeBoosts.clear();
        }

        starsObj = buildStars(universe.nodes);
        universeGroup.add(starsObj);
        starPosAttr = starsObj.geometry.getAttribute('position');
        pulseAttr = starsObj.geometry.getAttribute('aPulse');

        // sync current settings into the freshly-built materials.
        applySettings();

        // Hybrid layout: build per-galaxy force simulations on top of the
        // static log-spiral. Sims mutate node.local.{u,v}; projectAll then
        // pushes new world positions to the GPU buffers each frame.
        sims = buildPhysics(universe);

        // Build id→index map once so C#-forwarded pulses (by note id) can
        // O(1) find the right star slot in the buffer.
        idToIndex = new Map();
        for (let i = 0; i < universe.nodes.length; i++) {
            idToIndex.set(universe.nodes[i].id, i);
        }
        activePulses.clear();
        activeEdgeBoosts.clear();

        // fit camera: aim at centroid of all galaxy centers; back off enough
        // that all galaxies fit comfortably in the frustum.
        const ctr = new THREE.Vector3();
        let maxR = 0;
        for (const g of universe.galaxies) {
            ctr.x += g.center.x; ctr.y += g.center.y; ctr.z += g.center.z;
            const d = Math.hypot(g.center.x, g.center.y, g.center.z) + g.radius;
            if (d > maxR) maxR = d;
        }
        ctr.divideScalar(Math.max(1, universe.galaxies.length));
        const back = Math.max(220, maxR * 1.9);
        camera.position.set(ctr.x + back * 0.25, ctr.y + back * 0.55, ctr.z + back);
        controls.target.copy(ctr);
        controls.update();

        callbacks.onGalaxies?.(universe.galaxies);
        return universe;
    }

    function dispose() {
        for (const obj of [starsObj, edgesObj, nebulaObj]) {
            if (!obj) continue;
            // Each object is now a child of either universeGroup or nebulaGroup;
            // remove from whichever parent it actually has.
            obj.parent?.remove(obj);
            obj.traverse?.(o => {
                if (o.geometry) o.geometry.dispose();
                if (o.material) {
                    if (Array.isArray(o.material)) o.material.forEach(m => m.dispose());
                    else o.material.dispose();
                }
            });
            obj.geometry?.dispose?.();
            obj.material?.dispose?.();
        }
        starsObj = edgesObj = nebulaObj = null;
        edgeAlphaAttr = null;
        edgeBaseAlpha = null;
        edgeIntra = null;
        pulseEdgeBoost = null;
        activeEdgeBoosts.clear();
        activePulses.clear();
        // Drop component analysis — a new brain payload may have a totally
        // different graph topology, so the snapshot and component map
        // would be wrong. Recomputed lazily on next toggleIslands().
        nodeComponent = null;
        componentSize = null;
        islandStats   = null;
        islandBrightSnapshot = null;
        islandsOn = false;
        adjacency = null;
        starPosAttr = null;
        edgePosAttr = null;
        for (const s of sims) s.sim.stop();
        sims = [];
        hoverIndex = -1;
        selectedIndex = -1;
        universeGroup.rotation.set(0, 0, 0);
        nebulaGroup.rotation.set(0, 0, 0);
    }

    // ── physics (per-galaxy d3-force, hybrid with Fibonacci galaxy anchors) ──
    function buildPhysics(uni) {
        const out = [];

        // 1) group node indices by galaxy
        const byGalaxy = new Map();
        for (let i = 0; i < uni.nodes.length; i++) {
            const gi = uni.nodes[i].galaxyIdx;
            if (!byGalaxy.has(gi)) byGalaxy.set(gi, []);
            byGalaxy.get(gi).push(i);
        }

        for (const [gi, nodeIdxs] of byGalaxy) {
            const galaxy = uni.galaxies[gi];
            if (!galaxy) continue;

            // Build mutable particles in disk-local (u, v). d3-force expects
            // .x/.y on each node — we map those to local u/v. Local n
            // (disk thickness) stays static so the disk stays a disk.
            const localToGlobalIdx = nodeIdxs;
            const globalToLocalIdx = new Map();
            const particles = nodeIdxs.map((globalIdx, localIdx) => {
                globalToLocalIdx.set(globalIdx, localIdx);
                const n = uni.nodes[globalIdx];
                return {
                    x: n.local.u,
                    y: n.local.v,
                    nz: n.local.n,
                    radius: Math.max(1.4, n.size * 0.9)
                };
            });

            // Intra-galaxy edges only — cross-galaxy edges remain visual
            // lines but apply no force (otherwise galaxies would collapse
            // toward each other and the Fibonacci anchor would lose meaning).
            const links = [];
            for (const e of uni.edges) {
                const li = globalToLocalIdx.get(e.a);
                const lj = globalToLocalIdx.get(e.b);
                if (li == null || lj == null) continue;
                links.push({ source: li, target: lj });
            }

            // Force tuning:
            //   • charge  -16: repulsion, gentle so dense Programming galaxy
            //     doesn't explode beyond its disk radius
            //   • link distance 6, weak strength: pull connected notes close
            //   • center: gravity well at (0,0) of disk-local space
            //   • collide: prevent overlap at the rendered star scale
            //
            // alphaDecay 0.025 settles in ~213 ticks → ~3.5 s at 60 fps,
            // then sim.alpha() drops below alphaMin and we skip ticking.
            const sim = forceSimulation(particles)
                .force('charge', forceManyBody().strength(-16).distanceMax(galaxy.radius * 1.4))
                .force('link', forceLink(links).distance(6).strength(0.35))
                .force('center', forceCenter(0, 0).strength(0.05))
                .force('collide', forceCollide().radius(d => d.radius).strength(0.7))
                .alphaDecay(0.025)
                .alphaMin(0.005)
                .stop();   // we tick manually each frame

            out.push({
                galaxy,
                sim,
                particles,
                localToGlobalIdx,
                radius: galaxy.radius
            });
        }
        return out;
    }

    // Project all settled local positions back to world and upload to the
    // GPU buffers. Called each frame while any sim is still ticking; once
    // all sims hit alphaMin we skip this entirely.
    function projectAndUpload() {
        if (!starPosAttr || !universe) return;

        // 1) project each galaxy's particles → world; write into node.position
        for (const ps of sims) {
            const g = ps.galaxy;
            const r = g.radius * 1.05;   // soft clamp to disk radius
            for (let k = 0; k < ps.particles.length; k++) {
                const p = ps.particles[k];
                // Clamp particle to disk so repulsion can't shoot a node
                // into another galaxy. Quadratic damp near the boundary.
                const len = Math.hypot(p.x, p.y);
                if (len > r) {
                    const s = r / len;
                    p.x *= s; p.y *= s;
                    if (p.vx !== undefined) { p.vx *= 0.3; p.vy *= 0.3; }
                }
                const gi = ps.localToGlobalIdx[k];
                const node = universe.nodes[gi];
                node.local.u = p.x;
                node.local.v = p.y;
                const wx = g.center.x + g.basisU.x * p.x + g.basisV.x * p.y + g.normal.x * p.nz;
                const wy = g.center.y + g.basisU.y * p.x + g.basisV.y * p.y + g.normal.y * p.nz;
                const wz = g.center.z + g.basisU.z * p.x + g.basisV.z * p.y + g.normal.z * p.nz;
                node.position.x = wx;
                node.position.y = wy;
                node.position.z = wz;
                starPosAttr.array[3 * gi + 0] = wx;
                starPosAttr.array[3 * gi + 1] = wy;
                starPosAttr.array[3 * gi + 2] = wz;
            }
        }
        starPosAttr.needsUpdate = true;

        // 2) edges follow — each line segment's two endpoints from current
        //    star world positions.
        if (edgePosAttr && universe.edges.length) {
            for (let i = 0; i < universe.edges.length; i++) {
                const a = universe.nodes[universe.edges[i].a].position;
                const b = universe.nodes[universe.edges[i].b].position;
                edgePosAttr.array[6 * i + 0] = a.x;
                edgePosAttr.array[6 * i + 1] = a.y;
                edgePosAttr.array[6 * i + 2] = a.z;
                edgePosAttr.array[6 * i + 3] = b.x;
                edgePosAttr.array[6 * i + 4] = b.y;
                edgePosAttr.array[6 * i + 5] = b.z;
            }
            edgePosAttr.needsUpdate = true;
        }
    }

    function stepPhysics() {
        if (!sims.length) return;
        let anyHot = false;
        for (const ps of sims) {
            if (ps.sim.alpha() <= ps.sim.alphaMin()) continue;
            ps.sim.tick();
            anyHot = true;
        }
        if (anyHot) projectAndUpload();
    }

    function applySettings() {
        bloom.strength = settings.glow;
        if (starsObj) {
            const u = starsObj.material.uniforms;
            u.uMotion.value = settings.motion;
            u.uStarScale.value = settings.stars;
            u.uSizeScale.value = settings.size;
        }
        if (edgesObj) {
            const u = edgesObj.material.uniforms;
            u.uMotion.value = settings.motion;
            u.uStarScale.value = settings.stars;
            u.uEdgeAlpha.value = settings.edges;
        }
        applyDrift();
    }

    /**
     * Drift = "stars never fully settle". When > 0, every per-galaxy d3-force
     * sim runs with `alphaTarget(drift × 0.02)` so its alpha never drops to
     * zero — node positions perpetually adjust as forces balance. Stops
     * cold when drift = 0 (sim cools to alphaMin, stepPhysics no-ops).
     *
     * If sims have already cooled when the user nudges drift up, we re-heat
     * each one by setting alpha back up so it picks up where it left off.
     */
    function applyDrift() {
        if (!sims.length) return;
        const target = settings.drift * 0.02;
        for (const ps of sims) {
            ps.sim.alphaTarget(target);
            if (target > 0 && ps.sim.alpha() < target * 1.2) {
                // re-warm so the loop's "alpha > alphaMin" check passes
                ps.sim.alpha(Math.max(0.1, target * 2));
            }
        }
    }

    // Track-selected: each frame we lerp controls.target onto the selected
    // star's current world position (so the star stays at screen center even
    // as universeGroup rotates + physics drifts). Camera position follows by
    // the same delta to preserve user's zoom/orbit-angle.
    //
    // Skipped while a flyTo is in progress (fly drives both target + cam
    // explicitly). 'free'/'orbit'/'follow' modes all benefit equally.
    const _trackTmp = new THREE.Vector3();
    const _trackDelta = new THREE.Vector3();
    function stepTrackSelected() {
        if (!settings.lockSelected) return;
        if (selectedIndex === -1 || !universe || fly) return;
        const n = universe.nodes[selectedIndex];
        _trackTmp.set(n.position.x, n.position.y, n.position.z);
        universeGroup.updateMatrixWorld();
        _trackTmp.applyMatrix4(universeGroup.matrixWorld);
        // delta = how far target needs to slide this frame
        _trackDelta.subVectors(_trackTmp, controls.target).multiplyScalar(TRACK_LERP);
        controls.target.add(_trackDelta);
        camera.position.add(_trackDelta);  // keep relative offset → user's pan/zoom preserved
    }

    function setSize(w, h) {
        renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
        renderer.setSize(w, h, false);
        composer.setSize(w, h);
        bloom.setSize(w, h);
        camera.aspect = w / Math.max(1, h);
        camera.updateProjectionMatrix();
        if (starsObj) {
            starsObj.material.uniforms.uPixelRatio.value = window.devicePixelRatio || 1;
        }
    }

    function pickAtPointer(clientX, clientY) {
        if (!starsObj) return -1;
        const rect = canvas.getBoundingClientRect();
        ndc.x = ((clientX - rect.left) / rect.width) * 2 - 1;
        ndc.y = -((clientY - rect.top) / rect.height) * 2 + 1;
        raycaster.setFromCamera(ndc, camera);
        const hits = raycaster.intersectObject(starsObj, false);
        if (!hits.length) return -1;
        // hits are sorted by distance — distanceToRay isn't exposed in a
        // useful way, so pick the closest by camera distance.
        return hits[0].index ?? -1;
    }

    function setPointer(clientX, clientY) {
        lastPointer.x = clientX;
        lastPointer.y = clientY;
        lastPointer.valid = true;
    }
    function clearPointer() { lastPointer.valid = false; }

    function focusNode(idx, instant = false) {
        if (!universe || idx < 0 || idx >= universe.nodes.length) {
            selectedIndex = -1;
            if (starsObj) starsObj.material.uniforms.uSelectedIndex.value = -1;
            highlightConnectedEdges(-1);
            callbacks.onSelect?.(null);
            return;
        }
        selectedIndex = idx;
        if (starsObj) starsObj.material.uniforms.uSelectedIndex.value = idx;
        const n = universe.nodes[idx];
        highlightConnectedEdges(idx);

        // ease camera toward the star: target = star CURRENT world position
        // (universeGroup is rotating, so the layout-time position drifts).
        const target = new THREE.Vector3(n.position.x, n.position.y, n.position.z);
        universeGroup.updateMatrixWorld();
        target.applyMatrix4(universeGroup.matrixWorld);
        const desiredDist = 14 + n.size * 6;
        const fromCam = camera.position.clone().sub(controls.target).normalize();
        const newCamPos = target.clone().add(fromCam.multiplyScalar(desiredDist));
        flyTo(target, newCamPos, instant ? 0 : 0.65);

        callbacks.onSelect?.({
            index: idx,
            node: n,
            related: collectRelated(idx)
        });
    }

    function collectRelated(idx) {
        if (!universe?.edges?.length) return [];
        const out = [];
        for (const e of universe.edges) {
            if (e.a === idx) out.push(universe.nodes[e.b]);
            else if (e.b === idx) out.push(universe.nodes[e.a]);
        }
        return out;
    }

    // Selection-only alpha for one edge (B2 fix): kept as a pure function so
    // stepPulses can re-derive it each frame and composite with the live
    // pulse boost. No global writes here — callers either invalidate the
    // edge buffer (recomputeEdgeAlphas) or read this value inline.
    function selectionAlphaFor(i) {
        if (!edgeBaseAlpha || !universe) return 0.1;
        if (selectedIndex === -1) return edgeBaseAlpha[i];
        const e = universe.edges[i];
        if (e.a === selectedIndex || e.b === selectedIndex) return 0.85;
        return edgeBaseAlpha[i] * 0.35;
    }

    // Composite selection + decaying pulse into the GPU buffer. Cheap enough
    // to run every frame (one max+write per edge); we skip the work entirely
    // when nothing is moving and selection state hasn't changed.
    let _lastSelectionWriteIdx = -2;
    function recomputeEdgeAlphas(force) {
        if (!edgeAlphaAttr || !edgeBaseAlpha) return;
        const N = universe.edges.length;
        // Fast path: if no pulse boosts are live AND selection hasn't moved
        // since last write, the buffer is already correct.
        if (!force
            && activeEdgeBoosts.size === 0
            && _lastSelectionWriteIdx === selectedIndex) return;

        for (let i = 0; i < N; i++) {
            const sel = selectionAlphaFor(i);
            const boost = pulseEdgeBoost[i];
            // Pulse contribution: additive boost on top of base, peak ≈ 0.95.
            // We take max(sel, base+boost) so selection still dominates when
            // both apply — the user-intended highlight wins.
            const pulse = edgeBaseAlpha[i] + boost * 0.85;
            const a = Math.max(sel, pulse);
            edgeAlphaAttr.array[2 * i + 0] = a;
            edgeAlphaAttr.array[2 * i + 1] = a;
        }
        edgeAlphaAttr.needsUpdate = true;
        _lastSelectionWriteIdx = selectedIndex;
    }

    function highlightConnectedEdges(idx) {
        // selectedIndex is the canonical source of truth; this just kicks
        // the composite recompute.
        recomputeEdgeAlphas(true);
    }

    // ── camera flyTo ─────────────────────────────────────────────────
    let fly = null;
    function flyTo(targetVec, camVec, durationSec) {
        if (durationSec <= 0) {
            controls.target.copy(targetVec);
            camera.position.copy(camVec);
            controls.update();
            fly = null;
            return;
        }
        fly = {
            t0: performance.now() / 1000,
            dur: durationSec,
            fromTarget: controls.target.clone(),
            toTarget: targetVec.clone(),
            fromCam: camera.position.clone(),
            toCam: camVec.clone()
        };
    }
    function stepFly(now) {
        if (!fly) return;
        const t = Math.min(1, (now - fly.t0) / fly.dur);
        // ease-in-out cubic
        const k = t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
        controls.target.lerpVectors(fly.fromTarget, fly.toTarget, k);
        camera.position.lerpVectors(fly.fromCam, fly.toCam, k);
        if (t >= 1) fly = null;
    }

    function focusGalaxy(category) {
        if (!universe) return;
        const g = universe.galaxies.find(x => x.category === category);
        if (!g) return;
        // Galaxy center in current rotated world space.
        const target = new THREE.Vector3(g.center.x, g.center.y, g.center.z);
        universeGroup.updateMatrixWorld();
        target.applyMatrix4(universeGroup.matrixWorld);
        // pull camera back along the galaxy's "outward" normal (its center
        // vector from world origin) so we see the disk roughly face-on.
        const outward = target.length() > 0.001
            ? target.clone().normalize()
            : new THREE.Vector3(0, 0.4, 1).normalize();
        const dist = g.radius * 3.2 + 20;
        const camVec = target.clone().add(outward.multiplyScalar(dist));
        flyTo(target, camVec, 0.7);
    }

    function resetView() {
        focusNode(-1, true);
        if (!universe) return;
        const ctr = new THREE.Vector3();
        let maxR = 0;
        for (const g of universe.galaxies) {
            ctr.x += g.center.x; ctr.y += g.center.y; ctr.z += g.center.z;
            const d = Math.hypot(g.center.x, g.center.y, g.center.z) + g.radius;
            if (d > maxR) maxR = d;
        }
        ctr.divideScalar(Math.max(1, universe.galaxies.length));
        const back = Math.max(220, maxR * 1.9);
        flyTo(ctr, new THREE.Vector3(ctr.x + back * 0.25, ctr.y + back * 0.55, ctr.z + back), 0.7);
    }

    // ── render loop ──────────────────────────────────────────────────
    const clock = new THREE.Clock();
    let running = true;

    function tick() {
        if (!running) return;
        const dt = clock.getDelta();
        const now = performance.now() / 1000;

        // hover picking (cheap — only when pointer is on canvas).
        if (lastPointer.valid && starsObj) {
            const idx = pickAtPointer(lastPointer.x, lastPointer.y);
            if (idx !== hoverIndex) {
                hoverIndex = idx;
                starsObj.material.uniforms.uHoverIndex.value = idx;
                callbacks.onHover?.(idx === -1 ? null : {
                    index: idx,
                    node: universe.nodes[idx]
                });
            }
        } else if (hoverIndex !== -1) {
            hoverIndex = -1;
            if (starsObj) starsObj.material.uniforms.uHoverIndex.value = -1;
            callbacks.onHover?.(null);
        }

        if (starsObj) starsObj.material.uniforms.uTime.value = now;
        if (edgesObj) edgesObj.material.uniforms.uTime.value = now;

        // Settle the per-galaxy d3-force sims (no-op after they cool down).
        stepPhysics();

        // Decay live MCP pulses + edge arcs (no-op when none active).
        stepPulses(dt);

        // Lock selected star to screen center (no-op when nothing selected
        // or a flyTo is steering). Orbit mode is owned by OrbitControls.
        stepTrackSelected();

        // Motion: universe rotates slowly around Y; nebula spins a touch
        // faster on its own axis for parallax. While flying to a target,
        // the global rotation pauses so the camera doesn't have to chase
        // a moving point.
        const mo = settings.motion;
        if (mo > 0) {
            const flyPause = fly ? 0.15 : 1.0;
            universeGroup.rotation.y += dt * 0.020 * mo * flyPause;
            nebulaGroup.rotation.y  += dt * 0.045 * mo;
            nebulaGroup.rotation.x  += dt * 0.012 * mo;
        }

        stepFly(performance.now() / 1000);
        controls.update();
        composer.render(dt);
        requestAnimationFrame(tick);
    }
    requestAnimationFrame(tick);

    // events: hover/click. Both translate into index → callback.
    function onPointerMove(e) {
        setPointer(e.clientX, e.clientY);
    }
    function onPointerLeave() {
        clearPointer();
    }
    function onClick(e) {
        const idx = pickAtPointer(e.clientX, e.clientY);
        if (idx >= 0) focusNode(idx);
        else if (selectedIndex !== -1) focusNode(-1);
    }
    function onContextMenu(e) {
        // Right-click a star → walk its 2-hop neighbourhood as a sequenced
        // lightning wave. Suppress the browser context menu so the gesture
        // is captured cleanly. Right-click on empty space falls through to
        // OrbitControls (right-drag = pan), so we only preventDefault when
        // we actually picked a star.
        const idx = pickAtPointer(e.clientX, e.clientY);
        if (idx < 0) return;
        e.preventDefault();
        walkFromHere(idx, 2);
    }
    function onKey(e) {
        if (e.key === 'Escape') {
            if (selectedIndex !== -1) focusNode(-1);
            else resetView();
        }
    }
    canvas.addEventListener('pointermove', onPointerMove);
    canvas.addEventListener('pointerleave', onPointerLeave);
    canvas.addEventListener('click', onClick);
    canvas.addEventListener('contextmenu', onContextMenu);
    window.addEventListener('keydown', onKey);

    function destroy() {
        running = false;
        canvas.removeEventListener('pointermove', onPointerMove);
        canvas.removeEventListener('pointerleave', onPointerLeave);
        canvas.removeEventListener('click', onClick);
        canvas.removeEventListener('contextmenu', onContextMenu);
        window.removeEventListener('keydown', onKey);
        dispose();
        composer.dispose?.();
        renderer.dispose();
    }

    function resettle() {
        for (const ps of sims) ps.sim.alpha(1.0);
    }

    /**
     * Trigger a transient pulse on the star matching `noteId`. Also fans out
     * a few edge arcs to its top neighbours so the eye reads "current flowing
     * through this part of the brain right now". Tint by op: cyan = read,
     * magenta-orange = write.
     */
    function firePulse(noteId, op) {
        if (!pulseAttr || !idToIndex) return;
        const idx = idToIndex.get(noteId);
        if (idx == null) {
            // B3: rate-limited miss diagnostic. Silent fall-through is too
            // hard to debug when access-log ids drift away from brain-export.
            _missCount++;
            const now = performance.now();
            if (now - _lastMissLogAt > 30000) {
                console.warn(`[Universe] firePulse miss: ${_missCount} unmatched noteIds (most recent: "${noteId}"). brain-export may be stale — try re-export.`);
                _lastMissLogAt = now;
                _missCount = 0;
            }
            return;
        }
        // Record the start time and seed the buffer with the t=0 amplitude.
        // stepPulses will recompute every frame from the lightning envelope.
        // Re-firing on a star already lit just resets t0 → fresh flash.
        const now = performance.now();
        pulseAttr.array[idx] = lightningAmpStar(0);
        pulseAttr.needsUpdate = true;
        activePulses.set(idx, now);

        // Camera-follow mode: when a pulse fires, the camera flies to that
        // star automatically — the "AI is reading your brain right now"
        // framing. Limited to one fly at a time (overwrites any in-flight).
        if (settings.cameraMode === 'follow') {
            focusNode(idx);
        }

        // Edge arcs: pick up to MAX_ARCS_PER_PULSE incident edges and start
        // their lightning envelope. stepPulses composites the per-frame
        // amplitude into the GPU alpha buffer.
        if (!universe?.edges?.length || !pulseEdgeBoost) return;
        let count = 0;
        for (let i = 0; i < universe.edges.length && count < MAX_ARCS_PER_PULSE; i++) {
            const e = universe.edges[i];
            if (e.a !== idx && e.b !== idx) continue;
            pulseEdgeBoost[i] = lightningAmpEdge(0);
            activeEdgeBoosts.set(i, now);
            count++;
        }
    }

    /**
     * "Walk this concept" / "Trace this thought" — BFS from `start` (note
     * id OR star idx), pulse the seed immediately, then schedule layered
     * pulses outward at staggered delays so the eye reads it as a wave
     * radiating through the wiki-link graph. Same lightning envelope per
     * star — just sequenced.
     *
     * Returns a stats object the host UI can pipe into the status bar:
     *   { startIdx, startTitle, hops, totalReached, perHop: [N0,N1,N2,...] }
     */
    function walkFromHere(start, hops = 2, layerDelayMs = 180) {
        if (!universe || !pulseAttr) return null;

        // Resolve start: accept either string (note id) or number (idx).
        let startIdx = -1;
        if (typeof start === 'number') {
            startIdx = start;
        } else if (typeof start === 'string' && idToIndex) {
            const v = idToIndex.get(start);
            if (typeof v === 'number') startIdx = v;
        }
        if (startIdx < 0 || startIdx >= universe.nodes.length) return null;

        const adj = buildAdjacencyIfNeeded();
        const maxHops = Math.max(1, Math.min(5, hops | 0));

        // Layered BFS — collect indices reached at each hop distance.
        const visited = new Map();
        visited.set(startIdx, 0);
        const layers = [[startIdx]];
        let frontier = [startIdx];
        for (let h = 1; h <= maxHops; h++) {
            const next = [];
            for (const i of frontier) {
                const nb = adj[i];
                if (!nb) continue;
                for (const j of nb) {
                    if (visited.has(j)) continue;
                    visited.set(j, h);
                    next.push(j);
                }
            }
            if (next.length === 0) break;
            layers.push(next);
            frontier = next;
        }

        // Schedule the wave. Layer 0 fires synchronously so the source
        // star lights up the same frame the user clicked. Subsequent
        // layers cascade by layerDelayMs each — visible "current
        // travelling outward" effect, perfectly synced with the
        // lightning envelope's edge propagation delay.
        for (let h = 0; h < layers.length; h++) {
            const layerIdx = h;
            const layerNodes = layers[h];
            const fire = () => {
                for (const idx of layerNodes) {
                    const noteId = universe.nodes[idx]?.id;
                    if (noteId) firePulse(noteId);
                }
            };
            if (layerIdx === 0) fire();
            else setTimeout(fire, layerIdx * layerDelayMs);
        }

        const stats = {
            startIdx,
            startTitle: universe.nodes[startIdx].title,
            hops: layers.length - 1,
            totalReached: visited.size,
            perHop: layers.map(l => l.length)
        };
        callbacks.onWalk?.(stats);
        return stats;
    }

    // Re-evaluate live pulses against the lightning envelope and recompose
    // edge alpha. Called every frame; cheap when nothing is alive (early
    // exits via Map.size check). dt is unused now — the envelope is keyed
    // off wall-clock elapsed-since-trigger, so frame jitter doesn't change
    // the perceived flicker rhythm.
    function stepPulses(/* dt */) {
        if (!pulseAttr) return;
        const now = performance.now();

        // 1) per-star: lookup t0, compute envelope amplitude, write to GPU.
        //    Detach when the envelope returns 0 (window expired).
        if (activePulses.size > 0) {
            const toRemove = [];
            for (const [idx, t0] of activePulses) {
                const amp = lightningAmpStar(now - t0);
                if (amp <= 0) {
                    pulseAttr.array[idx] = 0;
                    toRemove.push(idx);
                } else {
                    pulseAttr.array[idx] = amp;
                }
            }
            for (const idx of toRemove) activePulses.delete(idx);
            pulseAttr.needsUpdate = true;
        }

        // 2) per-edge: same envelope, slightly faster window. When an edge
        //    drops out, recomputeEdgeAlphas restores its selection/base
        //    contribution automatically — no "restore" bug.
        if (activeEdgeBoosts.size > 0 && pulseEdgeBoost) {
            const toRemove = [];
            for (const [i, t0] of activeEdgeBoosts) {
                const amp = lightningAmpEdge(now - t0);
                if (amp <= 0) {
                    pulseEdgeBoost[i] = 0;
                    toRemove.push(i);
                } else {
                    pulseEdgeBoost[i] = amp;
                }
            }
            for (const i of toRemove) activeEdgeBoosts.delete(i);
        }

        // 3) recompose final edge alpha. The function early-outs when both
        //    no pulses are live AND selection hasn't moved since last write.
        recomputeEdgeAlphas(false);
    }

    function setMotion(v) {
        settings.motion = clamp(v, 0, 2);
        applySettings();
    }
    function setGlow(v) {
        settings.glow = clamp(v, 0, 1.5);
        applySettings();
    }
    function setStars(v) {
        settings.stars = clamp(v, 0.2, 1.5);
        applySettings();
    }
    function setStarSize(v) {
        settings.size = clamp(v, 0.3, 3.0);
        applySettings();
    }
    // ── Connected-component analysis ("Show islands") ────────────────
    //
    // The brain's wiki-link graph is treated as undirected for component
    // detection (an edge is an edge regardless of direction). Union-find
    // with path compression — O((N + E) · α(N)), one-shot on first toggle,
    // memoised until the next mount(). Result tells the user "these notes
    // are unreachable from your main knowledge cluster — link them or
    // accept them as orphans".
    let nodeComponent = null;       // Int32Array: nodeIdx → root
    let componentSize = null;       // Map<root, size>
    let islandStats   = null;       // {totalComponents, mainSize, islandCount, loneCount}
    let islandBrightSnapshot = null;// original aBrightness, restored on toggle-off
    let islandsOn = false;

    // Lazy undirected adjacency list — built once per mount, used by
    // walkFromHere. universe.edges has (a,b) once per pair so we expand
    // both directions here so BFS treats wiki-links as undirected.
    let adjacency = null;
    function buildAdjacencyIfNeeded() {
        if (adjacency || !universe) return adjacency;
        const N = universe.nodes.length;
        const adj = new Array(N);
        for (let i = 0; i < N; i++) adj[i] = [];
        for (const e of universe.edges || []) {
            adj[e.a].push(e.b);
            adj[e.b].push(e.a);
        }
        adjacency = adj;
        return adj;
    }

    function computeComponents() {
        if (!universe) return null;
        const N = universe.nodes.length;
        const parent = new Int32Array(N);
        for (let i = 0; i < N; i++) parent[i] = i;
        function find(x) {
            while (parent[x] !== x) {
                parent[x] = parent[parent[x]];   // path compression
                x = parent[x];
            }
            return x;
        }
        for (const e of universe.edges || []) {
            const ra = find(e.a), rb = find(e.b);
            if (ra !== rb) parent[ra] = rb;
        }
        const comp = new Int32Array(N);
        const sz = new Map();
        let mainSize = 0;
        for (let i = 0; i < N; i++) {
            const r = find(i);
            comp[i] = r;
            const next = (sz.get(r) || 0) + 1;
            sz.set(r, next);
            if (next > mainSize) mainSize = next;
        }
        let loneCount = 0;
        let islandCount = 0;
        for (const s of sz.values()) {
            if (s === 1) loneCount++;
            else if (s < mainSize) islandCount++;
        }
        nodeComponent = comp;
        componentSize = sz;
        islandStats = {
            totalComponents: sz.size,
            mainSize,
            islandCount,    // small clusters (2..mainSize-1)
            loneCount       // singletons (no edges at all)
        };
        return islandStats;
    }

    /**
     * Toggle the islands highlight: dim main-component stars to 30% and
     * boost any star NOT in the main component to 175% with the existing
     * colour palette. No shader changes — just rewrites aBrightness in
     * place. Returns the stats object so the caller can update status.
     */
    function toggleIslands(on) {
        if (!starsObj) return null;
        const brightAttr = starsObj.geometry.getAttribute('aBrightness');
        if (!brightAttr) return null;

        // First call: snapshot the original brightness so toggle-off can
        // restore byte-for-byte (never trust GPU-side state).
        if (!islandBrightSnapshot) {
            islandBrightSnapshot = new Float32Array(brightAttr.array);
        }

        islandsOn = !!on;
        const arr = brightAttr.array;
        if (!islandsOn) {
            arr.set(islandBrightSnapshot);
            brightAttr.needsUpdate = true;
            return islandStats || computeComponents();
        }

        if (!nodeComponent) computeComponents();
        const mainRoot = (() => {
            let bestR = -1, bestS = -1;
            for (const [r, s] of componentSize) {
                if (s > bestS) { bestS = s; bestR = r; }
            }
            return bestR;
        })();
        for (let i = 0; i < arr.length; i++) {
            const inMain = nodeComponent[i] === mainRoot;
            arr[i] = islandBrightSnapshot[i] * (inMain ? 0.30 : 1.75);
        }
        brightAttr.needsUpdate = true;
        return islandStats;
    }

    function getIslandStats() {
        if (!islandStats) computeComponents();
        return islandStats;
    }

    function setLightning(intensity, speed) {
        // Either argument may be undefined — preserves current value so a
        // single-arg call from the host is safe (e.g. only intensity slider
        // moved but speed slider stayed).
        if (typeof intensity === 'number') settings.lightning = clamp(intensity, 0, 2);
        if (typeof speed === 'number')     settings.lightningSpeed = clamp(speed, 0.25, 3);
    }
    function setEdgeAlpha(v) {
        settings.edges = clamp(v, 0, 2.0);
        applySettings();
    }
    function setDrift(v) {
        settings.drift = clamp(v, 0, 2.0);
        applyDrift();
    }
    function setCameraMode(m) {
        if (m !== 'free' && m !== 'orbit' && m !== 'follow') return;
        settings.cameraMode = m;
        // 'orbit' uses OrbitControls.autoRotate — built-in, smooth, auto-
        // pauses on user input. 'free' and 'follow' both leave autoRotate off.
        controls.autoRotate = (m === 'orbit');
    }

    /**
     * Black mode = truly black: pitch-black clear color, nebula sprites and
     * the background starfield hidden, fog density bumped so distant stars
     * fade to pure void. Nebula mode restores the radial gradient + sprites.
     */
    function setBackground(which) {
        const isBlack = which === 'black';
        settings.background = isBlack ? 'black' : 'nebula';
        renderer.setClearColor(isBlack ? 0x000000 : 0x02030a, 1);
        if (nebulaGroup) nebulaGroup.visible = !isBlack;
        if (starfieldObj) starfieldObj.visible = !isBlack;
        scene.fog.density = isBlack ? 0.0040 : 0.0025;
    }

    function setLockSelected(on) {
        settings.lockSelected = !!on;
    }

    /**
     * Pick a random camera angle around the current target. Distance stays
     * within the user-friendly orbit range; pitch is biased away from
     * straight-down so the galaxies stay readable.
     */
    function randomizeCamera() {
        if (!universe) return;
        const target = controls.target.clone();
        // Spherical coords: yaw ∈ [0, 2π), pitch ∈ [PI/6, 5PI/6] (avoid poles)
        const yaw   = Math.random() * Math.PI * 2;
        const pitch = (Math.PI / 6) + Math.random() * (4 * Math.PI / 6);
        // Pick a distance proportional to the cluster radius so the camera
        // always frames most of the universe.
        const baseDist = 220 + Math.random() * 180;
        const x = baseDist * Math.sin(pitch) * Math.cos(yaw);
        const y = baseDist * Math.cos(pitch);
        const z = baseDist * Math.sin(pitch) * Math.sin(yaw);
        const newCam = new THREE.Vector3(target.x + x, target.y + y, target.z + z);
        flyTo(target, newCam, 0.85);
    }

    /**
     * Snapshot camera state for persistence (localStorage). Just the world-
     * space tuple we need to recreate the view on next mount.
     */
    function snapshotCamera() {
        return {
            pos: { x: camera.position.x, y: camera.position.y, z: camera.position.z },
            tgt: { x: controls.target.x,  y: controls.target.y,  z: controls.target.z }
        };
    }

    function restoreCamera(snap) {
        if (!snap || !snap.pos || !snap.tgt) return;
        camera.position.set(snap.pos.x, snap.pos.y, snap.pos.z);
        controls.target.set(snap.tgt.x, snap.tgt.y, snap.tgt.z);
        controls.update();
    }
    function getSettings() { return { ...settings }; }

    return {
        mount,
        setSize,
        focusNode,
        focusGalaxy,
        resetView,
        resettle,
        firePulse,
        setMotion,
        setGlow,
        setStars,
        setStarSize,
        setEdgeAlpha,
        setLightning,
        toggleIslands,
        getIslandStats,
        walkFromHere,
        setDrift,
        setCameraMode,
        setBackground,
        setLockSelected,
        randomizeCamera,
        snapshotCamera,
        restoreCamera,
        getSettings,
        destroy
    };
}

function clamp(v, min, max) { return Math.min(max, Math.max(min, v)); }
