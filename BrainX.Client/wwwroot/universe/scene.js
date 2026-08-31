// BrainX Universe — three.js scene.
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
import { forceSimulation, forceManyBody, forceLink, forceCenter, forceCollide, forceX, forceY } from 'd3-force';
import { buildUniverse } from './layout.js';
import { loadPanorama } from './panorama.js';

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

// Shared soft round-dot texture for plain PointsMaterial layers (dust +
// starfield). Without a map, WebGL points rasterize as hard SQUARES — very
// visible once sizeAttenuation makes dust grow near the camera.
let _softDotTex = null;
function softDotTexture() {
    if (_softDotTex) return _softDotTex;
    const SIZE = 64;
    const c = document.createElement('canvas');
    c.width = c.height = SIZE;
    const ctx = c.getContext('2d');
    const grad = ctx.createRadialGradient(SIZE / 2, SIZE / 2, 0, SIZE / 2, SIZE / 2, SIZE / 2);
    grad.addColorStop(0.0, 'rgba(255,255,255,1)');
    grad.addColorStop(0.4, 'rgba(255,255,255,0.55)');
    grad.addColorStop(1.0, 'rgba(255,255,255,0)');
    ctx.fillStyle = grad;
    ctx.fillRect(0, 0, SIZE, SIZE);
    _softDotTex = new THREE.CanvasTexture(c);
    _softDotTex.colorSpace = THREE.SRGBColorSpace;
    return _softDotTex;
}

// Background starfield: two layers on a huge sphere for depth — a dense dim
// carpet plus a sparse layer of brighter stars with real astronomical color
// scatter (cool blue-white / warm amber, like actual stellar classes).
function buildStarfield() {
    const group = new THREE.Group();

    function layer(count, size, opacity, warmRatio) {
        const positions = new Float32Array(count * 3);
        const colors = new Float32Array(count * 3);
        const c = new THREE.Color();
        for (let i = 0; i < count; i++) {
            // uniformly on a sphere shell
            const u = Math.random(), v = Math.random();
            const theta = 2 * Math.PI * u;
            const phi = Math.acos(2 * v - 1);
            const r = 1100 + Math.random() * 200;
            positions[3 * i + 0] = r * Math.sin(phi) * Math.cos(theta);
            positions[3 * i + 1] = r * Math.sin(phi) * Math.sin(theta);
            positions[3 * i + 2] = r * Math.cos(phi);
            // Stellar tint: mostly cool blue-white, a fraction warm (K/M-class
            // amber) so the sky reads as real stars, not a flat blue wash.
            if (Math.random() < warmRatio) c.setHSL(0.07 + Math.random() * 0.05, 0.55, 0.72);
            else                           c.setHSL(0.58 + Math.random() * 0.09, 0.35, 0.78);
            colors[3 * i + 0] = c.r; colors[3 * i + 1] = c.g; colors[3 * i + 2] = c.b;
        }
        const geom = new THREE.BufferGeometry();
        geom.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        geom.setAttribute('color', new THREE.BufferAttribute(colors, 3));
        const mat = new THREE.PointsMaterial({
            map: softDotTexture(),
            vertexColors: true,
            size,
            sizeAttenuation: false,
            transparent: true,
            opacity,
            depthWrite: false,
            /* This shell sits 1100–1300 units out and scene.fog is FogExp2 at
               density 0.0025, so every one of these stars was arriving with a
               fog factor of 1 - e^-(3)^2 — erased to the fog colour, which is
               very nearly black. The layer was being built, uploaded and drawn
               every frame to contribute almost nothing. Background sky is not
               something fog should reach: fog exists to sink DISTANT GRAPH
               NODES into the void, and the sky is behind all of them. */
            fog: false,
            blending: THREE.AdditiveBlending
        });
        return new THREE.Points(geom, mat);
    }

    group.add(layer(2200, 0.7, 0.40, 0.18));   // dim carpet
    group.add(layer(340, 1.6, 0.65, 0.30));    // sparse bright accents
    return group;
}

/* ── The Milky Way ────────────────────────────────────────────────
 *
 * The sky had stars but no galaxy: a uniform scatter on a sphere reads as
 * "space" in the abstract, and the thing that makes a real night sky look
 * photographed is the band — a great circle of unresolved starlight with dark
 * dust cutting along it.
 *
 * Four features do the work, and leaving any one out is what makes a painted
 * Milky Way look painted:
 *   1. a BAND, brightest at the galactic equator and falling off with latitude
 *   2. DUST LANES — the Great Rift. The single most recognisable feature, and
 *      the one usually missing; without it the band is a fog bank
 *   3. an off-centre BULGE, wider and warmer, so the band is not a uniform
 *      stripe with the same cross-section everywhere
 *   4. RESOLVED STARS crowding the plane, so the glow and the star layer agree
 *      about where the galaxy is
 *
 * Drawn with canvas ops rather than a per-pixel loop: the page vendors its own
 * three.js and ships no image assets (see the 2026-07-10 overhaul), so the sky
 * has to be generated — and 2M pixels of JS at boot is a stall the compositor
 * cannot hide, while blurred gradient blobs are hardware work.
 */

/** Shared tilt for the band. The dome and the band's stars both take it, or
 *  the glow and the stars would describe two different galaxies. */
const MW_TILT = { x: 0.36, y: 0.62, z: 0.17 };

/** Idle rotation of the world about Y, rad/s. The graph and the sky share it. */
const SPIN = 0.020;

/* The sky, once the photograph arrives.
 *
 * The painted dome below is not thrown away — it is what is on screen for the
 * first second and a half, and what stays there if the file is missing, which
 * for a wallpaper matters more than it would for a page nobody leaves running.
 * BAND_STARS is why the swap is not just a texture assignment: those 1500
 * points exist to give the painted glow some resolvable stars, and a photograph
 * arrives with its own. Left at full strength you get two star fields at
 * slightly different densities over the same band, which reads as grain. Turned
 * down rather than off, because they sit 300 units inside the dome and are the
 * only thing giving the sky any parallax at all when the camera moves.
 */
const SKY_PHOTO = './sky/milkyway.jpg';
const BAND_STARS_PAINTED = 0.55;
const BAND_STARS_PHOTO = 0.22;

/* HOW BRIGHT THE SKY IS ALLOWED TO BE, and why the photograph gets less than
   the painting did. The dome is drawn ADDITIVELY, so every value in the image
   is light added to the whole view — and a photograph carries far more total
   light than a painted glow does, because the painting is mostly black by
   construction and the photograph has stars everywhere. Shipping it at the
   painting's 0.85, then grading it BRIGHTER on top, put the sky over the graph
   and the nodes stopped reading. Cut, and the contrast raised instead: pushing
   the voids to black takes light off the screen where it means nothing and
   leaves it where the band is. */
const SKY_DOME_PAINTED = 0.85;
const SKY_DOME_PHOTO = 0.55;
const SKY_GRADE = 'brightness(0.95) contrast(1.40) saturate(1.10)';

function milkyWayTexture() {
    const W = 2048, H = 1024;
    const c = document.createElement('canvas');
    c.width = W; c.height = H;
    const ctx = c.getContext('2d');

    // Additive blending: black IS transparent here, so the canvas starts as
    // empty sky and every stroke only ever adds light.
    ctx.fillStyle = '#000';
    ctx.fillRect(0, 0, W, H);

    /* Fixed seed. The sky is scenery, not content — it must look the same on
       every launch, or the app has a different night sky each time it opens
       and the owner cannot tell a redraw from a bug. */
    let seed = 0x5EED11;
    const rnd = () => ((seed = (seed * 1664525 + 1013904223) >>> 0) / 4294967296);
    const range = (a, b) => a + rnd() * (b - a);

    const midY = H / 2;
    // Galactic centre, off to one side. A band that is brightest dead centre
    // reads as a lighting effect; a real one has its core somewhere.
    const coreX = W * 0.3;

    /** Distance from the core along the wrapped horizontal axis, 0..1. */
    const fromCore = (x) => {
        const d = Math.abs(x - coreX);
        return Math.min(d, W - d) / (W / 2);
    };

    const blob = (x, y, rx, ry, color, blur) => {
        ctx.save();
        ctx.filter = `blur(${blur}px)`;
        ctx.translate(x, y);
        ctx.scale(1, ry / rx);
        const g = ctx.createRadialGradient(0, 0, 0, 0, 0, rx);
        g.addColorStop(0, color);
        g.addColorStop(1, 'rgba(0,0,0,0)');
        ctx.fillStyle = g;
        ctx.beginPath();
        ctx.arc(0, 0, rx, 0, Math.PI * 2);
        ctx.fill();
        ctx.restore();
    };

    ctx.globalCompositeOperation = 'lighter';

    // 1. The broad halo — very faint, very wide. This is what stops the band
    //    from having a hard edge against empty sky.
    blob(coreX, midY, W * 0.62, H * 0.30, 'rgba(70,86,140,0.30)', 90);
    blob(W * 0.8, midY, W * 0.40, H * 0.20, 'rgba(58,72,120,0.22)', 90);

    // 2. The band itself, as ~180 overlapping clumps along the equator. The
    //    jitter is the point: an even stripe looks airbrushed, and real
    //    unresolved starlight is lumpy at every scale.
    for (let i = 0; i < 180; i++) {
        const x = rnd() * W;
        const t = fromCore(x);                      // 0 at the core, 1 opposite
        const bulge = Math.exp(-t * t * 4.2);       // fat and bright near the core
        const y = midY + range(-1, 1) * H * (0.028 + 0.045 * bulge);
        const rx = range(W * 0.020, W * 0.075) * (0.65 + 0.7 * bulge);
        const ry = rx * range(0.16, 0.34);
        // Warm where the core's old stars dominate, cooler out along the arms.
        const warm = Math.max(0, bulge - 0.15);
        const r = Math.round(198 + 52 * warm);
        const g = Math.round(196 + 26 * warm);
        const b = Math.round(206 - 34 * warm);
        const a = (0.05 + 0.10 * bulge) * range(0.6, 1.25);
        blob(x, y, rx, ry, `rgba(${r},${g},${b},${a.toFixed(3)})`, range(14, 40));
    }

    // 3. The bulge — a single bright warm mass at the core, the anchor the eye
    //    lands on. Drawn last of the light passes so it sits on top.
    blob(coreX, midY, W * 0.115, H * 0.115, 'rgba(255,236,206,0.30)', 60);
    blob(coreX, midY, W * 0.055, H * 0.062, 'rgba(255,244,224,0.34)', 40);

    /* 4. Dust. Under additive blending a dark lane is not something you paint
          ON — it is light you take AWAY, so this carves the glow with
          destination-out. Painting grey over it would only make it brighter. */
    ctx.globalCompositeOperation = 'destination-out';

    // The Great Rift: a long, roughly continuous tear running along the plane,
    // built from overlapping strokes so its edges stay ragged.
    /* Blur has to stay well UNDER the lane's own thickness. The first cut of
       this drew 6–17px-tall lanes and then blurred them by 10–26px, which is
       not softening an edge, it is deleting the feature — measured at 3-6% of
       peak brightness, invisible against a band that varies more than that on
       its own. A real rift takes half the band out. */
    let riftY = midY + H * 0.012;
    for (let x = -W * 0.1; x < W * 1.1; x += W * 0.008) {
        riftY += range(-1, 1) * H * 0.005;
        riftY = Math.max(midY - H * 0.045, Math.min(midY + H * 0.055, riftY));
        const t = fromCore(x);
        // Most pronounced across the bright part; over faint sky there is
        // nothing left to subtract.
        const strength = 0.78 * Math.exp(-t * t * 2.6) + 0.14;
        blob(x, riftY, range(W * 0.022, W * 0.048), range(H * 0.011, H * 0.026),
             `rgba(0,0,0,${strength.toFixed(3)})`, range(5, 13));
    }

    // Finer mottling, above and below the rift, so the dust is not one lane.
    for (let i = 0; i < 170; i++) {
        const x = rnd() * W;
        const t = fromCore(x);
        const y = midY + range(-1, 1) * H * 0.055;
        blob(x, y, range(W * 0.008, W * 0.030), range(H * 0.007, H * 0.018),
             `rgba(0,0,0,${(0.42 * Math.exp(-t * t * 2.2) + 0.08).toFixed(3)})`,
             range(4, 11));
    }

    ctx.globalCompositeOperation = 'source-over';

    const tex = new THREE.CanvasTexture(c);
    tex.colorSpace = THREE.SRGBColorSpace;
    // Longitude wraps; latitude must not, or the poles smear across the seam.
    tex.wrapS = THREE.RepeatWrapping;
    tex.wrapT = THREE.ClampToEdgeWrapping;
    return tex;
}

/** @param {number} anisotropy from renderer.capabilities.getMaxAnisotropy() */
function buildMilkyWay(anisotropy = 1) {
    const group = new THREE.Group();

    const dome = new THREE.Mesh(
        new THREE.SphereGeometry(1600, 64, 32),
        new THREE.MeshBasicMaterial({
            map: milkyWayTexture(),
            side: THREE.BackSide,          // we are inside it
            transparent: true,
            blending: THREE.AdditiveBlending,
            depthWrite: false,
            depthTest: false,              // with renderOrder, always behind
            /* MUST be false. scene.fog is FogExp2 at density 0.0025, and this
               dome is 1600 units out: exp(-(0.0025*1600)^2) = e^-16. The sky
               would be mathematically erased and the bug would look like "the
               texture failed to load". */
            fog: false,
            opacity: SKY_DOME_PAINTED,
        })
    );
    dome.renderOrder = -10;
    dome.rotation.set(MW_TILT.x, MW_TILT.y, MW_TILT.z);
    group.add(dome);

    /* Stars crowding the plane. Without these the glow floats over a sky whose
       stars are spread evenly — two layers disagreeing about where the galaxy
       is, which the eye reads immediately even if it cannot name the fault. */
    const COUNT = 1500;
    const positions = new Float32Array(COUNT * 3);
    const colors = new Float32Array(COUNT * 3);
    const col = new THREE.Color();
    let s2 = 0xBA11D;
    const r2 = () => ((s2 = (s2 * 1664525 + 1013904223) >>> 0) / 4294967296);
    for (let i = 0; i < COUNT; i++) {
        const theta = 2 * Math.PI * r2();
        // Gaussian-ish latitude (sum of uniforms) so density falls off from the
        // plane instead of stopping at a hard edge.
        const lat = ((r2() + r2() + r2() - 1.5) / 1.5) * 0.30;
        const r = 1150 + r2() * 220;
        const cl = Math.cos(lat);
        positions[3 * i + 0] = r * cl * Math.cos(theta);
        positions[3 * i + 1] = r * Math.sin(lat);
        positions[3 * i + 2] = r * cl * Math.sin(theta);
        if (r2() < 0.26) col.setHSL(0.08 + r2() * 0.04, 0.45, 0.74);
        else             col.setHSL(0.58 + r2() * 0.08, 0.22, 0.82);
        colors[3 * i + 0] = col.r; colors[3 * i + 1] = col.g; colors[3 * i + 2] = col.b;
    }
    const geom = new THREE.BufferGeometry();
    geom.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    geom.setAttribute('color', new THREE.BufferAttribute(colors, 3));
    const bandStars = new THREE.Points(geom, new THREE.PointsMaterial({
        map: softDotTexture(),
        vertexColors: true,
        size: 0.85,
        sizeAttenuation: false,
        transparent: true,
        opacity: BAND_STARS_PAINTED,
        depthWrite: false,
        fog: false,                       // same reason as the dome
        blending: THREE.AdditiveBlending,
    }));
    bandStars.rotation.set(MW_TILT.x, MW_TILT.y, MW_TILT.z);
    group.add(bandStars);

    /* The owner's brightness knob, kept HERE because this is the only place
       that knows which sky is currently on the dome and therefore what "1.00"
       is supposed to mean. It is a multiplier on the tuned opacities above,
       not an opacity itself: the painted sky and the photograph need different
       amounts to look the same, and the slider must not change meaning under
       the user when the photograph finishes loading a second after the page. */
    let domeBase = SKY_DOME_PAINTED, starBase = BAND_STARS_PAINTED, brightness = 1;
    group.userData.setBrightness = (v) => {
        brightness = Math.max(0, Number.isFinite(v) ? v : 1);
        dome.material.opacity = domeBase * brightness;
        bandStars.material.opacity = starBase * brightness;
    };

    /* Swap the painted band for the photograph when it lands. The void the
       image fades out into is BLACK and not the page colour, because this dome
       is drawn with additive blending: black is the only value that adds
       nothing, and anything else would lift the whole sky by a constant. */
    loadPanorama(new URL(SKY_PHOTO, import.meta.url).href,
                 { voidColor: [0, 0, 0], top: 0.10, bottom: 0.10, filter: SKY_GRADE })
        .then((canvas) => {
            if (!canvas) return;                  // painted sky stands
            const tex = new THREE.CanvasTexture(canvas);
            tex.colorSpace = THREE.SRGBColorSpace;
            tex.wrapS = THREE.RepeatWrapping;
            tex.wrapT = THREE.ClampToEdgeWrapping;
            tex.anisotropy = anisotropy;
            dome.material.map?.dispose();         // the canvas we are replacing
            dome.material.map = tex;
            dome.material.needsUpdate = true;
            // Re-apply through the same knob, so whatever the owner had it set
            // to survives the swap instead of snapping back to the default.
            domeBase = SKY_DOME_PHOTO;
            starBase = BAND_STARS_PHOTO;
            group.userData.setBrightness(brightness);
        });

    return group;
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
    const coreCol = new THREE.Color();
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

        // Galactic bulge: a small, hot core at the disk center — real spiral
        // galaxies read as "bright nucleus + arms", and bloom turns this into
        // the anchor point the eye lands on. Color = galaxy hue pulled toward
        // white so it looks like dense old stars, not a colored lamp.
        coreCol.setHex(g.color).lerp(new THREE.Color(0xfff6e8), 0.55);
        const coreMat = new THREE.SpriteMaterial({
            map: tex,
            color: coreCol.clone(),
            transparent: true,
            opacity: 0.60,
            depthWrite: false,
            blending: THREE.AdditiveBlending
        });
        const core = new THREE.Sprite(coreMat);
        core.position.set(g.center.x, g.center.y, g.center.z);
        const cs = Math.max(6, g.radius * 0.9);
        core.scale.set(cs, cs, cs);
        core.userData.galaxy = g.category;
        group.add(core);
    }
    return group;
}

// Unresolved-star dust: thousands of tiny points scattered along each
// galaxy's two log-spiral arms (same math as layout.js) so a disk reads as
// a STAR SYSTEM — millions of faint suns — instead of only N note-stars
// floating in a void. One Points object for all galaxies; never pickable
// (raycasts only test starsObj) and static (no per-frame CPU cost).
function buildGalaxyDust(galaxies) {
    let total = 0;
    const counts = galaxies.map(g => {
        const c = Math.min(900, Math.max(140, Math.round(g.count * 14)));
        total += c;
        return c;
    });
    if (!total) return null;

    const positions = new Float32Array(total * 3);
    const colors = new Float32Array(total * 3);
    const col = new THREE.Color();
    const white = new THREE.Color(0xffffff);
    let w = 0;
    for (let gi = 0; gi < galaxies.length; gi++) {
        const g = galaxies[gi];
        const n = counts[gi];
        for (let k = 0; k < n; k++) {
            // Same two-arm log-spiral as the note layout, tighter jitter so
            // the dust TRACES the arms the anchored notes sit on.
            const t = Math.pow(Math.random(), 0.65);            // bias toward core
            const rim = g.radius * (0.06 + t * 1.02);
            const arm = (k % 2) * Math.PI;
            const swirl = Math.log(1 + rim) * 1.6;
            const theta = arm + swirl + (Math.random() - 0.5) * 0.55;
            const lx = rim * Math.cos(theta);
            const ly = rim * Math.sin(theta);
            const lz = (Math.random() + Math.random() + Math.random() - 1.5) * (g.radius / 9);

            positions[w * 3 + 0] = g.center.x + g.basisU.x * lx + g.basisV.x * ly + g.normal.x * lz;
            positions[w * 3 + 1] = g.center.y + g.basisU.y * lx + g.basisV.y * ly + g.normal.y * lz;
            positions[w * 3 + 2] = g.center.z + g.basisU.z * lx + g.basisV.z * ly + g.normal.z * lz;

            // Core dust glows warmer/brighter; rim dust cools to the galaxy hue.
            col.setHex(g.color).lerp(white, Math.max(0, 0.55 - t * 0.6));
            const dim = 0.35 + (1 - t) * 0.45;
            colors[w * 3 + 0] = col.r * dim;
            colors[w * 3 + 1] = col.g * dim;
            colors[w * 3 + 2] = col.b * dim;
            w++;
        }
    }

    const geom = new THREE.BufferGeometry();
    geom.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    geom.setAttribute('color', new THREE.BufferAttribute(colors, 3));
    const mat = new THREE.PointsMaterial({
        map: softDotTexture(),
        vertexColors: true,
        size: 1.1,
        sizeAttenuation: true,
        transparent: true,
        opacity: 0.55,
        depthWrite: false,
        blending: THREE.AdditiveBlending
    });
    return new THREE.Points(geom, mat);
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
        // the eye should pick up local clusters first. Rebalanced when the
        // edge-dedupe fix roughly DOUBLED the drawn edge count (2026-07-10):
        // per-edge alpha drops so total scene luminosity stays where the
        // original 0.18/0.07 tuning intended.
        const same = a.category === b.category;
        const base = same ? 0.12 : 0.035;
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

    // The galaxy the sky belongs to. Same lifetime as the starfield — built
    // once, never rebuilt per brain, hidden together with it in 'black' mode.
    const milkyWayObj = buildMilkyWay(renderer.capabilities.getMaxAnisotropy());

    /* ONE GROUP, and it turns with the universe.
       Both of these used to be added straight to the scene, which meant that
       while the graph did its slow idle rotation the sky sat perfectly still
       behind it. Drag the mouse and everything moved together — the camera was
       orbiting, so of course it did — but let go and only half the picture kept
       moving. A sky that is nailed down while the world in front of it turns is
       the one thing that gives away that it is a backdrop and not a place, and
       the fault only ever showed when nobody was touching anything.
       Rotated at exactly universeGroup's rate rather than a slower "parallax"
       one: a rigid rotation about Y turns everything through the same angle
       whatever its distance, so anything less is not depth, it is the sky
       slipping. The nebula still spins faster on its own axis, which is where
       the relative motion in the scene comes from. */
    const skyGroup = new THREE.Group();
    skyGroup.add(starfieldObj, milkyWayObj);
    scene.add(skyGroup);

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
        sky: 1.0,           // multiplier on the Milky Way dome; 0 = off, 1 = as tuned
        size:  1.0,         // uSizeScale — star size multiplier
        edges: 1.0,         // uEdgeAlpha — edge alpha multiplier
        drift: 0.0,         // 0 = freeze after settle; >0 = keep sims simmering forever
        lightning: 1.0,         // 0 = disable pulses, 1 = default lightning, 2 = blinding
        lightningSpeed: 1.0,    // 0.5 = slow majestic strike, 1 = default, 2 = frantic flicker
        cameraMode: 'free', // 'free' | 'orbit' | 'follow' | 'random'
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
    let dustObj = null;
    let hoverIndex = -1;
    let selectedIndex = -1;
    // Animation-loop control. _rafHandle = current requestAnimationFrame id
    // (so pauseAnimation can cancel it); _animationPaused gates the
    // re-schedule at the end of tick(). See pauseAnimation/resumeAnimation
    // exported below — used by the host to auto-pause when a fullscreen
    // window covers the wallpaper.
    let _rafHandle = 0;
    let _animationPaused = false;
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

    // ── Convergence flash ──────────────────────────────────────────────
    //
    // The one moment this universe actually has: every galaxy's d3-force sim
    // starts hot, the stars visibly stream inward for ~3.5 s, and then they
    // stop. Until now that ending was silent — the motion simply ceased, and
    // the frame the brain finished forming looked exactly like the frame
    // after it. This marks it.
    //
    // Two layers, both riding machinery that already exists:
    //
    //   • a WAVE of per-star lightning, seeded from the rim inward, so the
    //     light travels the way the stars just travelled and arrives at the
    //     core last. Outward would read as an explosion; inward reads as
    //     gathering, which is what actually happened.
    //   • a bloom surge over the whole scene, cresting just after the wave
    //     lands. The bloom is what makes it a flash of LIGHT rather than a
    //     thousand stars each getting brighter on their own.
    //
    // The wave cannot be scheduled by writing future timestamps into
    // activePulses: lightningAmpStar returns 0 for a negative elapsed time
    // and stepPulses detaches anything reading 0, so a star scheduled for
    // later would be dropped on the very next frame. Hence its own queue,
    // pre-sorted, drained by a cursor.
    const CONVERGE_WAVE_MS   = 620;    // rim → core travel time
    const CONVERGE_GLOW_MS   = 1150;   // bloom surge, outlasts the wave
    const CONVERGE_GLOW_PEAK = 0.55;   // added on top of settings.glow at the crest
    const CONVERGE_CREST     = 0.38;   // fraction of the surge spent rising
    let converge = null;        // { t0, order: Int32Array, at: Float32Array, cursor, peak }

    /* ── Settling batches ────────────────────────────────────────────────
     *
     * WHY THIS REPLACED A SINGLE BOOLEAN. The flash used to hang off one
     * global edge detector: "were any sims hot last frame, are none hot now".
     * That is exactly right for the one event it was written for — the brain
     * assembling itself on load — and useless for every other one, because it
     * can only ever describe the whole universe at once.
     *
     * The brain is not only organised at startup. It is organised all day:
     * an agent writes a note, the gardener re-bakes bundles and fills
     * embeddings while the owner is away. Those are real events with a real
     * SCOPE — one galaxy, or all of them — and the picture should say which.
     *
     * So a batch is "the set of galaxies that one cause just disturbed". It
     * completes when every galaxy in it has come to rest, and completing is
     * what fires the flash, over exactly those stars. Load registers a batch
     * of all sims, which reproduces the old behaviour precisely; a note being
     * written registers a batch of one.
     *
     * WHY A BATCH AND NOT PER-GALAXY. Fifteen galaxies re-heated together
     * settle at slightly different times. Flashing each as it stops is
     * fifteen events where there was one thing — the stutter the original
     * design went out of its way to avoid. Waiting for the last one keeps a
     * cause and its effect one to one.
     */
    let batches = [];           // [{ sims: Set, deadline }]
    let simOfNode = null;       // global star index → its sim, for scoping
    /** Re-heat alpha for a re-organisation. Not 1.0: this is a settling, not
     *  a rebuild, and a full re-heat throws the layout apart hard enough that
     *  the eye reads it as the graph breaking rather than tidying. */
    const REORG_ALPHA = 0.28;
    /** Agents write in bursts. Collect ids for this long so three notes in
     *  two seconds are ONE re-organisation with one flash, not three. */
    const REORG_COALESCE_MS = 700;
    /** A batch can only complete by going cold, and with Drift > 0 no sim
     *  ever does. Without this they would pile up forever, holding memory and
     *  firing nothing. Expiring silently is right: nothing arrived. */
    const BATCH_MAX_MS = 30000;
    let pendingReorg = null;    // { ids: Set, timer }

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

        // Static spiral-arm dust — inside universeGroup (NOT nebulaGroup) so
        // it rotates in lock-step with the note stars it traces. Hidden in
        // 'black' background mode along with the other decorative layers.
        dustObj = buildGalaxyDust(universe.galaxies);
        if (dustObj) {
            dustObj.visible = settings.background !== 'black';
            universeGroup.add(dustObj);
        }

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
        buildSimIndex();
        // The assembly is itself a batch — the whole sky, disturbed by one
        // cause. Registering it here rather than special-casing "first settle"
        // is what makes load and every later re-organisation the same code
        // path, and therefore impossible to drift apart.
        batches = [{ sims: new Set(sims), deadline: performance.now() + BATCH_MAX_MS }];
        syncWorkAmbient();     // a job that outlived the previous scene resumes
        // Springs arrive once per recompute and outlive any number of mounts;
        // without this a re-index would silently drop the semantic layer and
        // the galaxies would quietly go back to structure-only.
        if (semanticSprings) applySemanticSprings();

        // Build id→index map once so C#-forwarded pulses (by note id) can
        // O(1) find the right star slot in the buffer.
        idToIndex = new Map();
        for (let i = 0; i < universe.nodes.length; i++) {
            idToIndex.set(universe.nodes[i].id, i);
        }
        activePulses.clear();
        activeEdgeBoosts.clear();
        // A new payload builds new sims, hot. Clearing the edge detector here
        // is what lets the next settle be recognised as a settle — left true
        // from a previous universe, the very first frame of this one would
        // look like an arrival that had already happened.
        converge = null;
        batches = [];
        cancelPendingReorg();

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
        for (const obj of [starsObj, edgesObj, nebulaObj, dustObj]) {
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
        starsObj = edgesObj = nebulaObj = dustObj = null;
        edgeAlphaAttr = null;
        edgeBaseAlpha = null;
        edgeIntra = null;
        pulseEdgeBoost = null;
        activeEdgeBoosts.clear();
        activePulses.clear();
        // Disposing mid-flash must not strand the bloom at its crest — the
        // next thing mounted would inherit a scene lit 2× brighter than the
        // owner's slider says.
        converge = null;
        batches = [];
        cancelPendingReorg();
        simOfNode = null;
        // The ambient interval outlives `universe` unless it is stopped here —
        // it would keep firing at a disposed scene forever, harmlessly but
        // forever. syncWorkAmbient re-arms it after the next mount if the job
        // that started it is still running.
        universe = null;
        syncWorkAmbient();
        bloom.strength = settings.glow;
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
                    // Anchor = the layout-time log-spiral position. A weak
                    // spring back to it keeps the two spiral arms readable
                    // after the force sim settles — without it, charge +
                    // link forces smear the disk into a featureless blob.
                    u0: n.local.u,
                    v0: n.local.v,
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
                // Spiral-arm anchor: weak spring toward each node's original
                // log-spiral slot. Strong enough that the arms survive the
                // settle, weak enough that linked notes still cluster.
                .force('anchorU', forceX(d => d.u0).strength(ANCHOR_STRENGTH))
                .force('anchorV', forceY(d => d.v0).strength(ANCHOR_STRENGTH))
                .alphaDecay(0.025)
                .alphaMin(0.005)
                .stop();   // we tick manually each frame

            out.push({
                galaxy,
                sim,
                particles,
                localToGlobalIdx,
                globalToLocalIdx,   // kept for the semantic springs
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

        // A batch fires when every galaxy IT disturbed has come to rest —
        // which is not the same question as "is anything moving", because a
        // note written during the gardener's pass must not steal the
        // gardener's flash, nor be swallowed by it.
        if (batches.length) {
            const now = performance.now();
            for (let i = batches.length - 1; i >= 0; i--) {
                const b = batches[i];
                let hot = false;
                for (const ps of b.sims) {
                    if (ps.sim.alpha() > ps.sim.alphaMin()) { hot = true; break; }
                }
                if (hot) {
                    if (now > b.deadline) batches.splice(i, 1);   // Drift on: never arrives
                    continue;
                }
                batches.splice(i, 1);
                startConvergenceFlash([...b.sims]);
            }
        }

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
    let _lastWriteHadPulses = false;
    function recomputeEdgeAlphas(force) {
        if (!edgeAlphaAttr || !edgeBaseAlpha) return;
        const N = universe.edges.length;
        const hasPulses = activeEdgeBoosts.size > 0;
        // Fast path: skip work only when no pulses are live AND the previous
        // write also had no pulses AND selection hasn't moved. The
        // `_lastWriteHadPulses` guard is critical — without it, the frame
        // where the last pulse expires writes pulseEdgeBoost[i]=0 into the
        // CPU array but the early-exit blocks the upload, so the GPU buffer
        // stays pinned at boosted alpha values until selection changes.
        if (!force
            && !hasPulses
            && !_lastWriteHadPulses
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
        _lastWriteHadPulses = hasPulses;
    }

    function highlightConnectedEdges(idx) {
        // selectedIndex is the canonical source of truth; this just kicks
        // the composite recompute.
        recomputeEdgeAlphas(true);
    }

    // ── camera flyTo ─────────────────────────────────────────────────
    let fly = null;
    // Home state captured when entering 'follow' mode. When the user
    // toggles follow OFF (mode → free/orbit/random), the camera flies
    // back to this exact position so they don't end up stranded on the
    // last pulsed star. Null when not in follow mode.
    let _followHomeTarget = null;
    let _followHomeCam = null;

    // Mirror-mode sync state.
    //   _mirrorBroadcastTimer : master-side. ~100 ms interval that posts
    //     current camera target+position back through the host bridge so
    //     C# can fan it out to slave wallpaper instances.
    //   _mirrorIsSlave        : slave-side. When true, this instance is
    //     receiving state from the master and must NOT broadcast.
    let _mirrorBroadcastTimer = null;
    let _mirrorIsSlave = false;

    // Follow-mode idle return: after a pulse-triggered fly, wait this many
    // ms with no NEW pulse before drifting back to the home pose. New
    // pulses reset the timer (chain behaviour). On mode-exit the timer is
    // cleared so the mode-exit handler's flyTo doesn't get clobbered.
    let _followIdleTimer = null;
    const FOLLOW_IDLE_RETURN_MS = 3000;
    function scheduleFollowIdleReturn() {
        if (_followIdleTimer) clearTimeout(_followIdleTimer);
        _followIdleTimer = setTimeout(() => {
            _followIdleTimer = null;
            // Only return if STILL in follow mode AND we have a home pose
            // recorded. If the user changed mode meanwhile, setCameraMode's
            // exit handler already flew us home — don't double-fly.
            if (settings.cameraMode === 'follow' && _followHomeTarget && _followHomeCam) {
                flyTo(_followHomeTarget, _followHomeCam, 0.7);
            }
        }, FOLLOW_IDLE_RETURN_MS);
    }
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

        // The arrival flash, if the sims just finished. Must sit between
        // these two: it seeds into activePulses, and stepPulses is what
        // writes them to the GPU.
        stepConvergence(now * 1000);

        // Decay live MCP pulses + edge arcs (no-op when none active).
        stepPulses(dt);

        // Peer halos — scale-in / fade-out / activity pulses (no-op when
        // no peers connected). Cheap: linear in peers.size, no GPU work
        // beyond the standard sprite material update.
        stepPeers(now * 1000);

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
            const spin = dt * SPIN * mo * flyPause;
            universeGroup.rotation.y += spin;
            // The sky turns through the SAME angle, off the same variable, so
            // the two can never be edited apart. It is one world turning.
            skyGroup.rotation.y += spin;
            nebulaGroup.rotation.y  += dt * 0.045 * mo;
            nebulaGroup.rotation.x  += dt * 0.012 * mo;
        }

        stepFly(performance.now() / 1000);
        controls.update();
        composer.render(dt);
        // Honor the host pause flag — when the wallpaper is fully covered
        // (e.g. fullscreen game on top of our monitor) the host sends
        // {type:'pauseRender'} → we set _animationPaused = true → the
        // rAF chain stops here. resumeAnimation() re-kicks it.
        if (!_animationPaused) _rafHandle = requestAnimationFrame(tick);
    }
    _rafHandle = requestAnimationFrame(tick);

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
        // Through heatAndWatch so the manual button gets the arrival flash the
        // automatic paths get. A re-settle you asked for is still an arrival.
        heatAndWatch(sims, 1.0);
    }

    // ── Peer halos (Join Brain visualization) ────────────────────────────
    //
    // Each remote peer connected to the hub is drawn as a glowing halo
    // sprite on a "join ring" orbiting outside the main note galaxy. The
    // ring sits in the X-Z plane at PEER_RING_RADIUS so peers float
    // around the user's brain like nearby stars. Layer is attached to the
    // top-level scene (not universeGroup) so peers DON'T spin with the
    // notes — that way the user's perspective on "who's here" stays
    // visually stable even when the camera rotates the brain.
    //
    // Color: deterministic per address (HSL hue from address hash) so the
    // same peer always lights the same color. Size: ~6 world units —
    // small enough that the brain stays the focal point, big enough that
    // peers register on first glance. Bloom does the heavy lifting for
    // "glow".
    const PEER_RING_RADIUS = 240;
    const PEER_RING_Y_JITTER = 28;
    const peerLayer = new THREE.Group();
    peerLayer.name = 'peerLayer';
    scene.add(peerLayer);
    const peers = new Map(); // address → { sprite, color, joinedAt, fading, scaleTarget, scaleCurrent }

    function _hashStrToUnit(s) {
        // Cheap deterministic 0..1 from a string. xor-fold 32-bit FNV variant —
        // enough quality for visual placement, not for crypto.
        let h = 2166136261 >>> 0;
        for (let i = 0; i < s.length; i++) {
            h ^= s.charCodeAt(i);
            h = Math.imul(h, 16777619) >>> 0;
        }
        return (h >>> 0) / 0xFFFFFFFF;
    }

    function _peerColorFromAddress(addr) {
        // HSL → RGB via THREE.Color. Vivid (S=0.85, L=0.6) so each peer
        // pops against the deep-space background.
        const hue = _hashStrToUnit(addr);
        const c = new THREE.Color().setHSL(hue, 0.85, 0.6);
        return c;
    }

    function _peerPositionFromAddress(addr) {
        // Two independent hashes: one for angle on the ring, one for Y
        // jitter so peers don't all sit on a perfect line.
        const angle = _hashStrToUnit(addr) * Math.PI * 2;
        const yPhase = _hashStrToUnit(addr + '|y') * 2 - 1; // -1..1
        return new THREE.Vector3(
            Math.cos(angle) * PEER_RING_RADIUS,
            yPhase * PEER_RING_Y_JITTER,
            Math.sin(angle) * PEER_RING_RADIUS
        );
    }

    // Build the halo sprite texture once and share across all peers — much
    // cheaper than per-peer textures, and the per-peer color comes from
    // sprite.material.color which multiplies into the texture's white.
    function _buildHaloTexture() {
        const size = 128;
        const cnv = document.createElement('canvas');
        cnv.width = cnv.height = size;
        const ctx = cnv.getContext('2d');
        // Radial gradient: hot white core → color falloff → transparent.
        const grad = ctx.createRadialGradient(size / 2, size / 2, 0, size / 2, size / 2, size / 2);
        grad.addColorStop(0.0, 'rgba(255,255,255,1.0)');
        grad.addColorStop(0.25, 'rgba(255,255,255,0.85)');
        grad.addColorStop(0.55, 'rgba(255,255,255,0.35)');
        grad.addColorStop(1.0, 'rgba(255,255,255,0)');
        ctx.fillStyle = grad;
        ctx.fillRect(0, 0, size, size);
        const tex = new THREE.CanvasTexture(cnv);
        tex.colorSpace = THREE.SRGBColorSpace;
        return tex;
    }
    const _peerHaloTexture = _buildHaloTexture();

    // Label sprite: per-peer canvas texture with display name + short
    // address. Floats next to the halo, billboarded so it always faces
    // the camera.
    function _buildPeerLabel(displayName, shortAddr, color) {
        const w = 320, h = 96;
        const cnv = document.createElement('canvas');
        cnv.width = w; cnv.height = h;
        const ctx = cnv.getContext('2d');
        // Pill-shaped background for legibility against the nebula.
        ctx.fillStyle = 'rgba(8,4,18,0.78)';
        ctx.beginPath();
        const r = 18;
        ctx.moveTo(r, 0);
        ctx.lineTo(w - r, 0); ctx.quadraticCurveTo(w, 0, w, r);
        ctx.lineTo(w, h - r); ctx.quadraticCurveTo(w, h, w - r, h);
        ctx.lineTo(r, h); ctx.quadraticCurveTo(0, h, 0, h - r);
        ctx.lineTo(0, r); ctx.quadraticCurveTo(0, 0, r, 0);
        ctx.closePath(); ctx.fill();
        // Coloured stripe so the label visually ties to the halo.
        ctx.fillStyle = '#' + color.getHexString();
        ctx.fillRect(0, 0, 6, h);
        // Display name + address.
        ctx.fillStyle = '#FFFFFF';
        ctx.font = 'bold 26px "Segoe UI", Arial';
        ctx.textBaseline = 'top';
        ctx.fillText(displayName || '(brain)', 18, 12);
        ctx.fillStyle = '#B7A6D8';
        ctx.font = '18px "Cascadia Code", Consolas, monospace';
        ctx.fillText(shortAddr, 18, 52);
        const tex = new THREE.CanvasTexture(cnv);
        tex.colorSpace = THREE.SRGBColorSpace;
        const mat = new THREE.SpriteMaterial({ map: tex, transparent: true, depthWrite: false });
        const spr = new THREE.Sprite(mat);
        spr.scale.set(40, 12, 1); // world units; sized to read at ring distance
        return spr;
    }

    /**
     * Add (or re-add) a peer halo. Idempotent — calling twice with the
     * same address just refreshes the display name / color and re-triggers
     * the scale-in animation so the user gets visual confirmation.
     */
    function addPeer(peerInfo) {
        if (!peerInfo || !peerInfo.address) return;
        const addr = String(peerInfo.address);
        const color = _peerColorFromAddress(addr);
        const pos = _peerPositionFromAddress(addr);

        let entry = peers.get(addr);
        if (!entry) {
            // Glow sprite — bigger, color-tinted halo.
            const haloMat = new THREE.SpriteMaterial({
                map: _peerHaloTexture,
                color,
                transparent: true,
                depthWrite: false,
                blending: THREE.AdditiveBlending
            });
            const halo = new THREE.Sprite(haloMat);
            halo.scale.set(0.01, 0.01, 0.01);
            halo.position.copy(pos);

            const shortAddr = addr.length > 22 ? addr.slice(0, 22) + '…' : addr;
            const label = _buildPeerLabel(peerInfo.displayName, shortAddr, color);
            label.position.copy(pos);
            label.position.y += 14; // float above the halo

            peerLayer.add(halo);
            peerLayer.add(label);
            entry = {
                halo, label, color,
                joinedAt: performance.now(),
                scaleCurrent: 0.01,
                scaleTarget: 14,    // final halo scale (world units)
                fading: false,
                pulseUntil: 0
            };
            peers.set(addr, entry);
        } else {
            // Re-join: refresh visuals + retrigger the scale-in pop so the
            // user sees a clear "they're back" cue. Reset scaleTarget too
            // in case the peer was mid-fade (which decays scaleTarget toward 0).
            entry.color = color;
            entry.halo.material.color.copy(color);
            entry.scaleCurrent = entry.scaleCurrent * 0.5; // ease back a bit so the pop reads
            entry.scaleTarget = 14;
            entry.fading = false;
            entry.joinedAt = performance.now();
        }
        // Brief scale-pop so even on the first frame the user notices
        // something appeared.
        entry.pulseUntil = performance.now() + 350;
    }

    /**
     * Remove a peer. Fades out smoothly over ~450 ms in stepPeers and then
     * disposes the resources — preserves a "they left" cue without a
     * jarring pop. If a peer with the same address rejoins during the
     * fade, addPeer() cancels the fading flag and they come back.
     */
    function removePeer(address) {
        const entry = peers.get(String(address));
        if (entry) entry.fading = true;
    }

    /**
     * Brief flash on a peer's halo — used when a share request to/from this
     * peer succeeds (or fails — caller picks color via overrideColor).
     * Visualizes data flow without redrawing the universe.
     */
    function pulsePeerActivity(address, overrideColor) {
        const entry = peers.get(String(address));
        if (!entry) return;
        if (overrideColor) {
            // Temporary color flash (e.g. red for share-denied). Restored
            // on the next pulse cycle by step's color-lerp.
            try { entry.halo.material.color.set(overrideColor); } catch {}
        }
        entry.pulseUntil = performance.now() + 500;
    }

    // Per-frame peer animation — scale-in on join, fade-out on leave,
    // scale-pop during activity pulses. Called from the main render loop
    // (stepPeers is hooked into the animate() function below).
    function stepPeers(now) {
        if (peers.size === 0) return;
        const PULSE_SCALE = 1.6;
        for (const [addr, entry] of peers) {
            // Pulse amplitude — eases up then down between pulseUntil-500 and pulseUntil.
            const remaining = entry.pulseUntil - now;
            let pulse = 0;
            if (remaining > 0) {
                // tri-wave: 0 → 1 → 0 across the 500 ms window
                const t = 1 - remaining / 500;
                pulse = Math.sin(t * Math.PI);
            }
            // Smoothly approach scaleTarget; ease-out so the join pop is
            // snappy without being jittery.
            entry.scaleCurrent += (entry.scaleTarget - entry.scaleCurrent) * 0.18;
            const s = entry.scaleCurrent * (1 + pulse * (PULSE_SCALE - 1));

            // Fade-out: shrink target to 0, then dispose when small.
            if (entry.fading) {
                entry.scaleTarget *= 0.86;
                entry.halo.material.opacity *= 0.88;
                entry.label.material.opacity *= 0.88;
                if (entry.scaleCurrent < 0.5) {
                    peerLayer.remove(entry.halo);
                    peerLayer.remove(entry.label);
                    entry.halo.material.dispose();
                    if (entry.label.material.map) entry.label.material.map.dispose();
                    entry.label.material.dispose();
                    peers.delete(addr);
                    continue;
                }
            } else {
                entry.halo.material.opacity = 0.92;
                entry.label.material.opacity = 0.95;
            }

            entry.halo.scale.set(s, s, s);
            // Label scale stays constant (it's read as text, not visual mass);
            // its position floats slightly with the halo so the pair tracks
            // together but the label doesn't balloon during pulses.
            entry.label.position.y = entry.halo.position.y + 14 + pulse * 1.2;
        }
    }

    /**
     * Trigger a transient pulse on the star matching `noteId`. Also fans out
     * a few edge arcs to its top neighbours so the eye reads "current flowing
     * through this part of the brain right now". Tint by op: cyan = read,
     * magenta-orange = write.
     */
    // Core star-flash: seed the lightning envelope on star `idx`, fan out a
    // few edge arcs, and (when focus=true) fly the follow-camera to it.
    // Shared by firePulse (exact node) + firePulseRandom (node-less fallback).
    function pulseStarAtIndex(idx, focus) {
        if (!pulseAttr || idx == null || idx < 0) return;
        // Record the start time and seed the buffer with the t=0 amplitude.
        // stepPulses recomputes every frame from the lightning envelope.
        // Re-firing on a star already lit just resets t0 → fresh flash.
        const now = performance.now();
        pulseAttr.array[idx] = lightningAmpStar(0);
        pulseAttr.needsUpdate = true;
        activePulses.set(idx, now);

        // Camera-follow mode: fly to the touched star — the "AI is reading
        // your brain right now" framing. Suppressed for fallback pulses
        // (focus=false) so node-less MCP calls don't yank the camera to a
        // random star on every brain_stats / brain_list tick.
        if (focus && settings.cameraMode === 'follow') {
            focusNode(idx);
            scheduleFollowIdleReturn();
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
            // A stale id must NOT read as "pulse dead" — the user wants every
            // MCP call visible. Fall back to a random star (no camera move).
            firePulseRandom(op);
            return;
        }
        pulseStarAtIndex(idx, true);
    }

    // Node-less MCP fallback: flash a random star (no camera move) so every
    // MCP call — including brain_stats / brain_list / brain_create_note that
    // carry no node_id — stays visibly "alive" (user spec: ทุก MCP call กระพริบ).
    function firePulseRandom(op) {
        if (!pulseAttr || !universe?.nodes?.length) return;
        const idx = Math.floor(Math.random() * universe.nodes.length);
        pulseStarAtIndex(idx, false);
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

    /**
     * Build the rim→core wave and start the bloom surge. Called once, from
     * the frame the last simulation cools.
     *
     * Scheduling is done in DISK-LOCAL coordinates, not world ones: each
     * particle's (x, y) is already its offset from its own galaxy's centre,
     * and dividing by that galaxy's radius normalises every galaxy to the
     * same 0..1 rim→core axis. So a dense 400-star galaxy and a sparse
     * 20-star one crest together instead of the big one finishing while the
     * small one is still lighting up — which would read as a stutter rather
     * than one event.
     */
    /**
     * @param {Array} which  the galaxies this flash belongs to. Defaults to
     *   all of them, which is the load/resettle case.
     *
     * SCOPE IS THE MESSAGE. A note being written disturbs one galaxy, and if
     * that fired the same whole-sky flash as the brain assembling itself, the
     * picture would be lying about how much just happened. The wave covers
     * only the stars that moved, and the bloom surge is scaled by their share
     * of the sky — so a small change reads small and the gardener finishing a
     * full pass reads like the arrival it is, off one code path.
     */
    function startConvergenceFlash(which = sims) {
        // settings.lightning is the owner's existing "how loud are flashes"
        // control, including 0 = off. A new effect does not get to ignore it.
        if (!pulseAttr || !universe || !which.length || settings.lightning <= 0) return;

        const pairs = [];
        for (const ps of which) {
            const r = Math.max(1e-3, ps.radius);
            for (let k = 0; k < ps.particles.length; k++) {
                const p = ps.particles[k];
                const d = Math.min(1, Math.hypot(p.x, p.y) / r);   // 0 = core, 1 = rim
                pairs.push([ps.localToGlobalIdx[k], (1 - d) * CONVERGE_WAVE_MS]);
            }
        }
        if (!pairs.length) return;

        // Sorted once so the per-frame drain is a cursor advance rather than
        // a scan of every star on every frame of the wave.
        pairs.sort((a, b) => a[1] - b[1]);
        const order = new Int32Array(pairs.length);
        const at    = new Float32Array(pairs.length);
        for (let i = 0; i < pairs.length; i++) { order[i] = pairs[i][0]; at[i] = pairs[i][1]; }

        // Square-rooted, not linear: one galaxy in fifteen is 7% of the stars
        // but nothing like 7% of the event, and a surge that faint is
        // indistinguishable from no surge at all. The floor is what keeps the
        // smallest galaxy still legible as an arrival.
        const share = Math.max(0.35, Math.sqrt(pairs.length / Math.max(1, universe.nodes.length)));
        // Never dip below a surge already in flight. Two galaxies settling a
        // second apart are two honest events, but restarting the envelope at a
        // smaller peak while the first is still cresting reads as the light
        // FAILING, not as a second arrival. The wave still restarts — new
        // stars do have to light — only the bloom refuses to go backwards.
        const peak = Math.max(CONVERGE_GLOW_PEAK * share, converge?.peak ?? 0);
        converge = { t0: performance.now(), order, at, cursor: 0, peak };
    }

    /** Global star index → the sim that owns it. Built with the sims. */
    function buildSimIndex() {
        simOfNode = new Map();
        for (const ps of sims)
            for (const gi of ps.localToGlobalIdx) simOfNode.set(gi, ps);
    }

    /**
     * Something in the brain actually changed — let the galaxies it touched
     * re-settle, and flash when they arrive.
     *
     * This is the whole point of the batch machinery: motion that happens all
     * the time carries no information, and motion that happens when something
     * changed carries all of it. Perpetual drift (the Drift slider) looks
     * alive and says nothing — d3-force at low alpha is a wobble around a
     * solution it already found. This moves only when there is a reason.
     *
     * @param {string[]} noteIds  ids of the notes that changed
     * @param {{alpha?: number}} opts
     */
    function reorganize(noteIds, opts = {}) {
        if (!sims.length || !idToIndex || !noteIds?.length) return;
        if (!pendingReorg) pendingReorg = { ids: new Set(), alpha: 0, timer: null };
        for (const id of noteIds) pendingReorg.ids.add(id);
        pendingReorg.alpha = Math.max(pendingReorg.alpha, opts.alpha ?? REORG_ALPHA);
        // Restart the window on every arrival: a burst of writes is ONE
        // re-organisation, and the flash belongs at the end of the burst.
        clearTimeout(pendingReorg.timer);
        pendingReorg.timer = setTimeout(commitReorganize, REORG_COALESCE_MS);
    }

    function cancelPendingReorg() {
        if (!pendingReorg) return;
        clearTimeout(pendingReorg.timer);
        pendingReorg = null;
    }

    function commitReorganize() {
        const req = pendingReorg;
        pendingReorg = null;
        if (!req || !sims.length || !universe) return;
        if (!simOfNode) buildSimIndex();

        const touched = new Set();
        for (const id of req.ids) {
            const idx = idToIndex.get(id);
            if (idx == null) continue;          // stale export; the pulse path already warns
            const ps = simOfNode.get(idx);
            if (ps) touched.add(ps);
        }
        if (!touched.size) return;
        heatAndWatch([...touched], req.alpha);
    }

    /**
     * Re-heat a set of galaxies and register the batch that will flash when
     * they have all come to rest.
     *
     * `alpha()` rather than `alphaTarget()`: a target keeps the sim warm
     * forever, which is Drift's job and would mean the batch never completes.
     * Setting alpha gives it energy that decays on the same curve as the
     * original settle, so a re-organisation LOOKS like the assembly it is a
     * smaller version of.
     */
    function heatAndWatch(list, alpha = REORG_ALPHA) {
        if (!list.length) return;
        for (const ps of list) {
            // max, never overwrite: a galaxy still settling from a moment ago
            // must not be cooled down by a smaller nudge arriving on top.
            if (ps.sim.alpha() < alpha) ps.sim.alpha(alpha);
        }
        // Fold into a batch already watching the same cause instead of opening
        // a second one — two batches over overlapping galaxies would flash
        // twice for what the owner did once.
        const now = performance.now();
        const open = batches.find(b => list.every(ps => b.sims.has(ps)));
        if (open) { open.deadline = now + BATCH_MAX_MS; return; }
        // And the other direction, which the first cut of this missed: a note
        // written a second before the gardener finishes leaves a one-galaxy
        // batch open INSIDE the all-galaxy one. Nothing contained the new list,
        // so a second batch opened, and that galaxy — settling on the same
        // curve as every other — would flash small on its own and then again
        // with the rest. Two flashes, half a second apart, for one arrival:
        // exactly the stutter this design exists to avoid. The bigger cause
        // swallows the smaller.
        const kept = [];
        for (const b of batches) {
            let inside = true;
            for (const ps of b.sims) if (!list.includes(ps)) { inside = false; break; }
            if (!inside) kept.push(b);
        }
        batches = kept;
        batches.push({ sims: new Set(list), deadline: now + BATCH_MAX_MS });
    }

    /* ── Semantic springs — the meaning the 3D view never had ───────────
     *
     * Embeddings have nudged the WPF layout since 2026-04-25: pairs of notes
     * whose vectors sit above cosine 0.55 pull toward each other at ~12% of a
     * wiki-link's strength, so notes ABOUT the same thing end up near each
     * other even with no link between them. Only PhysicsEngine ever applied
     * them. This view — the one the product leads with — had structural forces
     * only, which is the same information Obsidian's graph has.
     *
     * THE PROBLEM WITH PORTING THEM STRAIGHT. This layout is one sim PER
     * GALAXY, and measured on this brain — 1,522 notes, wiki-linked pairs
     * excluded exactly as SemanticSpringComputer excludes them — only 30.8% of
     * springs join two notes in the same galaxy. 69.2% cross. Adding them as
     * ordinary links inside each sim would therefore drop seven tenths of the
     * feature while looking like it worked.
     *
     * And the missing seven tenths cannot become a force here, for the reason
     * the file already gives about cross-galaxy edges: pulling galaxies toward
     * each other collapses the Fibonacci placement, and the placement is what
     * says "these are different categories". Truth about relatedness must not
     * be bought with a lie about separation.
     *
     * WHAT THIS SHIPS is the 30.8% that can be said honestly: an extra link
     * force inside each galaxy at SEMANTIC_RATIO of the structural one, so
     * notes about the same thing sit closer even with no link between them.
     * That is the WPF behaviour, unchanged, finally in this view.
     *
     * The other 69.2% is measured, counted, and deliberately NOT drawn — see
     * the long note further down for what was built, what it measured, and why
     * a position cannot carry that claim without lying about it.
     */
    const SEMANTIC_RATIO = 0.12;   // matches PhysicsEngine.SemanticSpringStrength
    const SEMANTIC_FLOOR = 0.55;   // matches SemanticSpringComputer.SimilarityThreshold
    /** Anchor spring strength. Shared with buildPhysics: the re-seat below
     *  rebuilds these forces, and a second copy of the number here would be a
     *  silent retune of the spiral arms the first time anyone edited one. */
    const ANCHOR_STRENGTH = 0.055;
    let semanticSprings = null;    // kept so a re-mount can re-apply them

    /**
     * @param {Array<{a: string, b: string, s: number}>} springs
     */
    function applySemanticSprings(springs) {
        if (Array.isArray(springs)) semanticSprings = springs;
        if (!semanticSprings || !sims.length || !universe || !idToIndex) return;

        // Strongest cross-galaxy affinity per node, and the intra-galaxy pairs
        // per sim. One pass; the spring list is ~5 per note.
        const bestCross = new Map();          // global idx → { sim, otherGalaxyIdx, s }
        const intra = new Map();              // sim → [{ source, target, s }]
        for (const sp of semanticSprings) {
            const ia = idToIndex.get(sp.a), ib = idToIndex.get(sp.b);
            if (ia == null || ib == null) continue;
            const sa = simOfNode?.get(ia), sb = simOfNode?.get(ib);
            if (!sa || !sb) continue;
            const s = +sp.s || 0;
            if (s < SEMANTIC_FLOOR) continue;
            if (sa === sb) {
                if (!intra.has(sa)) intra.set(sa, []);
                intra.get(sa).push({
                    source: sa.globalToLocalIdx.get(ia),
                    target: sa.globalToLocalIdx.get(ib), s });
                continue;
            }
            // Cross: each end leans toward the OTHER end's galaxy.
            for (const [self, other, selfSim] of [[ia, sb, sa], [ib, sa, sb]]) {
                const cur = bestCross.get(self);
                if (!cur || s > cur.s) bestCross.set(self, { sim: selfSim, other: other.galaxy, s });
            }
        }

        for (const ps of sims) {
            const links = intra.get(ps) ?? [];
            // Replaced wholesale rather than merged into the structural link
            // force: a second force under its own name leaves the tuned
            // structural one untouched, and re-applying is then idempotent.
            ps.sim.force('semantic', links.length
                ? forceLink(links).distance(6)
                    .strength(l => 0.35 * SEMANTIC_RATIO * l.s)
                : null);
        }

        /* THE CROSS-GALAXY HALF IS NOT SHOWN, AND THAT IS THE FINDING.
         *
         * It was built: each node's angle inside its own disk turned toward
         * the galaxy it is most related to, radius untouched so "distance from
         * the core" went on meaning importance, and the disk clamp guaranteed
         * no note could leave its category. It applied cleanly — mean angle to
         * the related galaxy went 1.575 -> 1.373 rad on the first tick, which
         * is exactly the lean that was asked for.
         *
         * Then the settle ate it. 1.373 -> 1.540 by tick 100, 1.546 by tick
         * 800, against 1.571 for random: 14% of what was applied survived.
         * That is not a tuning problem. These disks are packed, charge and
         * collide decide where a node ends up, and one note cannot hold a
         * position 13 degrees around the rim while its neighbours are pressed
         * against it. Leaning harder is eroded proportionally; holding it with
         * a stronger anchor combs the spiral arms into mush.
         *
         * So the channel cannot carry the claim, and shipping it anyway would
         * have been the exact failure this design is trying to avoid: motion
         * that looks like it means something while being mostly packing noise.
         * A viewer would read affinity off a position that is 86% arbitrary.
         *
         * The 69% of springs that cross galaxies still deserve to be seen —
         * but as something that states relatedness WITHOUT making a claim
         * about position, category or importance. A drawn edge does that; a
         * coordinate cannot. That is the next piece, not this one.
         */

        // A force added to a cold sim does nothing at all — the layout only
        // changes if something makes it settle again. This is also the honest
        // reading of the event: the semantic field just landed, so the brain
        // re-arranges and arrives, exactly as it does when a note is written.
        heatAndWatch(sims, REORG_ALPHA);

        /* Returned, not logged. Every part of this can fail by doing nothing —
         * ids that no longer match the export, springs that all sit on the
         * similarity floor and therefore lean by nothing, a galaxy whose
         * neighbour happens to lie along its own normal. Each of those looks
         * exactly like "the feature is off", and the only way to tell them
         * apart from outside is a count. */
        return {
            springs: semanticSprings.length,
            intraPairs: [...intra.values()].reduce((n, a) => n + a.length, 0),
            crossPairsNotShown: bestCross.size,
        };

    }

    /* ── Long jobs over the whole brain, made visible ────────────────────
     *
     * Two of these exist and they behave identically, so they get one path
     * rather than two that drift:
     *
     *   garden   `brainx-mcp garden` runs unattended once a day while the
     *            owner is away — re-bakes stale bundles, fills missing
     *            embeddings, audits the vault.
     *   reindex  the vault is re-scanned into the graph. The app's own 2D
     *            view has always shaken on this (`_dashPhysics.Disturb(kick)`
     *            right after IndexVaultAsync); the 3D universe was the only
     *            surface that sat still through the same event.
     *
     * WHILE ONE RUNS the brain is being read note by note, so it is shown the
     * way every other note-touch is shown — a star lighting up — just slow and
     * unfocused. Reusing the pulse rather than inventing an effect is the
     * point: that flicker already means "something touched a note", and that
     * is exactly what is happening.
     *
     * WHEN THE LAST ONE FINISHES every galaxy re-settles and the full flash
     * fires. A SET rather than a flag because the two can overlap — a re-index
     * kicked off while the gardener is still going would otherwise stop the
     * ambience early and fire the arrival twice, once for a pass that had not
     * actually finished.
     */
    const WORK_TICK_MS = 900;
    const workJobs = new Set();
    let workTimer = null;

    /**
     * @param {string} job   'garden' | 'reindex'
     * @param {boolean} on
     */
    function setBrainWork(job, on) {
        const before = workJobs.size;
        if (on) workJobs.add(job || 'work'); else workJobs.delete(job || 'work');
        syncWorkAmbient();
        // Falling edge of the LAST job: the work is done. Everything it
        // touched settles at once, which is the one case where the whole-sky
        // flash is the plain truth.
        if (before > 0 && workJobs.size === 0 && sims.length) heatAndWatch(sims, REORG_ALPHA);
    }

    /**
     * Start or stop the ambient flicker to match the job set.
     *
     * Also called from mount, because a re-index or a garden pass can outlive
     * the scene it started under — the brain payload it produces is what
     * re-mounts us. Without this the ambience would die at the swap and never
     * come back, and the arrival flash would land with nothing having led up
     * to it.
     */
    function syncWorkAmbient() {
        const want = workJobs.size > 0 && !!universe;
        if (want === !!workTimer) return;
        if (want) {
            workTimer = setInterval(() => {
                if (settings.lightning > 0) firePulseRandom('work');
            }, WORK_TICK_MS);
        } else {
            clearInterval(workTimer);
            workTimer = null;
        }
    }

    /**
     * Per-frame: release the stars whose turn has come, and drive the bloom.
     *
     * Runs BETWEEN stepPhysics and stepPulses in tick() — stars seeded here
     * are picked up by the same stepPulses pass on the same frame, so a star
     * never spends a frame flagged-but-dark.
     */
    function stepConvergence(now) {
        if (!converge) return;
        const elapsed = now - converge.t0;

        while (converge.cursor < converge.order.length && converge.at[converge.cursor] <= elapsed) {
            // t0 = now, not the scheduled time: a frame that arrives late
            // should start the envelope late, not start it already half
            // burnt down. Dropped frames cost smoothness, never brightness.
            activePulses.set(converge.order[converge.cursor], now);
            converge.cursor++;
        }

        const g = elapsed / CONVERGE_GLOW_MS;
        if (g >= 1) {
            converge = null;
            bloom.strength = settings.glow;   // hand the bloom back untouched
            return;
        }
        // Fast rise, slow fall — light decays, it does not ramp down. Squared
        // so the shoulders stay near the baseline and the crest is the only
        // part the eye registers as an event.
        const k = g < CONVERGE_CREST
            ? g / CONVERGE_CREST
            : 1 - (g - CONVERGE_CREST) / (1 - CONVERGE_CREST);
        bloom.strength = settings.glow + (converge.peak ?? CONVERGE_GLOW_PEAK) * k * k * settings.lightning;
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
    /**
     * How bright the Milky Way behind the graph is. 0 turns it off entirely
     * and leaves the plain starfield; 1 is the tuned default. Separate from
     * `stars`, which is the note-stars — these are the two things that fight
     * each other for the same screen, so they get a knob each.
     */
    function setSky(v) {
        settings.sky = clamp(v, 0, 1.5);
        milkyWayObj?.userData.setBrightness?.(settings.sky);
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
        if (m !== 'free' && m !== 'orbit' && m !== 'follow' && m !== 'random') return;
        const prev = settings.cameraMode;
        settings.cameraMode = m;
        // 'orbit' uses OrbitControls.autoRotate — built-in, smooth, auto-
        // pauses on user input. 'free' and 'follow' both leave autoRotate off.
        controls.autoRotate = (m === 'orbit');
        // 'random' = persistent showcase mode: every 9-14s pick a random
        // action (fly to a node, zoom out, fly to a random angle, brief
        // orbit). Designed for the wallpaper / idle showcase use case
        // where the user wants the universe to drift on its own.
        if (prev === 'random' && m !== 'random') stopRandomMode();
        if (m === 'random') startRandomMode();

        // 'follow' = camera flies to each pulsed star. Snapshot the
        // camera's current pose when ENTERING follow so we can restore it
        // when the user toggles follow OFF — otherwise the camera is
        // stranded on whichever star last pulsed.
        if (prev !== 'follow' && m === 'follow') {
            _followHomeTarget = controls.target.clone();
            _followHomeCam    = camera.position.clone();
        } else if (prev === 'follow' && m !== 'follow') {
            // Clear any pending idle-return timer so it doesn't fire after
            // we've already flown home via this mode-exit handler.
            if (_followIdleTimer) { clearTimeout(_followIdleTimer); _followIdleTimer = null; }
            if (_followHomeTarget && _followHomeCam) {
                flyTo(_followHomeTarget, _followHomeCam, 0.7);
            }
            _followHomeTarget = null;
            _followHomeCam    = null;
        }
    }

    // ── Random camera mode ──────────────────────────────────────────────
    // Cycles through "showcase" actions on a jittered timer so a left-on
    // wallpaper / monitor doesn't sit on the same shot. Picks actions by
    // weighted random; an early-out check on settings.cameraMode keeps the
    // queue from outliving a mode switch.
    let _randomTimer = null;
    function startRandomMode() {
        stopRandomMode();
        const actions = [
            { w: 35, fn: flyToRandomNode },             // zoom in on a star
            { w: 25, fn: () => fitToScreen({ padding: 1.35, duration: 1.4 }) }, // zoom out
            { w: 25, fn: randomizeCamera },             // random orbit angle
            { w: 15, fn: () => orbitBriefly(12000) }    // 12s of auto-rotate
        ];
        const total = actions.reduce((s, a) => s + a.w, 0);
        function pickAndQueue() {
            let r = Math.random() * total;
            for (const a of actions) {
                r -= a.w;
                if (r <= 0) {
                    try { a.fn(); } catch (e) { console.warn('[random-cam] action failed', e); }
                    break;
                }
            }
            // Re-queue only if still in random mode (mode-switch could've
            // landed mid-action; respect it on the next tick).
            if (settings.cameraMode === 'random') {
                _randomTimer = setTimeout(pickAndQueue, 9000 + Math.random() * 5000);
            }
        }
        // Fire one immediately so the user sees feedback on the click.
        pickAndQueue();
    }
    function stopRandomMode() {
        if (_randomTimer) { clearTimeout(_randomTimer); _randomTimer = null; }
        // Orbit may have been left on by orbitBriefly; clear it unless the
        // current mode is actually orbit.
        if (settings.cameraMode !== 'orbit') controls.autoRotate = false;
    }
    function flyToRandomNode() {
        if (!universe || !universe.nodes.length) return;
        const idx = Math.floor(Math.random() * universe.nodes.length);
        focusNode(idx);
    }
    function orbitBriefly(durationMs) {
        controls.autoRotate = true;
        setTimeout(() => {
            // Don't override if user (or another action) switched to orbit
            // proper, or if random was disabled while we were spinning.
            if (settings.cameraMode === 'random') controls.autoRotate = false;
        }, durationMs);
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
        if (milkyWayObj) milkyWayObj.visible = !isBlack;
        if (dustObj) dustObj.visible = !isBlack;
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

    /**
     * Auto-fit: reframe the camera so every node sits inside the viewport.
     * Uses LIVE world positions (post-physics) and the current viewport
     * aspect, so the result is tight regardless of how far the simulation
     * has drifted or how the user has resized the window.
     *
     * @param {object} [opts]
     * @param {number} [opts.padding=1.18]  Extra room around the bounding sphere; 1.0 = touch edges.
     * @param {number} [opts.duration=0.85] flyTo duration in seconds; pass 0 for snap.
     * @param {boolean} [opts.keepDirection=true] If true, preserve current camera angle; if false, use the default 3/4 viewing angle.
     */
    function fitToScreen(opts = {}) {
        if (!universe || !universe.nodes.length) return;
        const padding  = opts.padding  ?? 1.18;
        const duration = opts.duration ?? 0.85;
        const keepDir  = opts.keepDirection !== false;

        // 1) Bounding sphere: centroid + max-radial-distance. Centroid handles
        //    asymmetric layouts (one huge galaxy, several tiny ones) better
        //    than an AABB midpoint would.
        let cx = 0, cy = 0, cz = 0;
        const n = universe.nodes.length;
        for (let i = 0; i < n; i++) {
            const p = universe.nodes[i].position;
            cx += p.x; cy += p.y; cz += p.z;
        }
        const inv = 1 / n;
        cx *= inv; cy *= inv; cz *= inv;

        let maxDist2 = 0;
        for (let i = 0; i < n; i++) {
            const p = universe.nodes[i].position;
            const dx = p.x - cx, dy = p.y - cy, dz = p.z - cz;
            const d2 = dx * dx + dy * dy + dz * dz;
            if (d2 > maxDist2) maxDist2 = d2;
        }
        const sphereR = Math.sqrt(maxDist2) || 1;

        // Take universeGroup rotation into account — node.position values are
        // in pre-rotation local space; the visible centroid lives in world.
        universeGroup.updateMatrixWorld();
        const centroid = new THREE.Vector3(cx, cy, cz).applyMatrix4(universeGroup.matrixWorld);

        // 2) Camera distance from FOV + aspect. For a perspective camera the
        //    vertical half-FOV maps directly; horizontal half-FOV is
        //    atan(tan(v/2) * aspect). Use the SMALLER tangent so the sphere
        //    fits in BOTH dimensions — otherwise a wide window crops the top
        //    and bottom of the cluster.
        const fovV = camera.fov * Math.PI / 180;
        const tanV = Math.tan(fovV / 2);
        const aspect = camera.aspect || 1;
        const tanH = tanV * aspect;
        const tan = Math.min(tanV, tanH);
        const distance = (sphereR * padding) / tan;

        // 3) Direction = (cam − target) normalized. Reusing it keeps the
        //    user's angle intact (just zooms in/out + recentres). If we're
        //    on first mount or the camera coincides with the target, fall
        //    back to a pleasant 3/4 view.
        let dir = camera.position.clone().sub(controls.target);
        if (!keepDir || dir.lengthSq() < 1e-6) dir.set(0.25, 0.55, 1);
        dir.normalize();
        const camVec = centroid.clone().add(dir.multiplyScalar(distance));
        flyTo(centroid, camVec, duration);
    }

    function getSettings() { return { ...settings }; }

    // Auto-pause: when the host says the wallpaper is fully covered (e.g.
    // fullscreen game), stop the rAF loop. Saves ~30-60% of the WebView2
    // process's GPU/CPU on idle. resumeAnimation() restarts the loop;
    // because animation state lives in module-level closures + three.js
    // scene graph (not destroyed), resume picks up smoothly without
    // re-initialization.
    function pauseAnimation() {
        if (_animationPaused) return;
        _animationPaused = true;
        if (_rafHandle) { cancelAnimationFrame(_rafHandle); _rafHandle = 0; }
    }
    function resumeAnimation() {
        if (!_animationPaused) return;
        _animationPaused = false;
        // Reset THREE.Clock so the first post-resume frame doesn't
        // attribute the full pause duration as elapsed time → universe
        // wouldn't jump-rotate by hours of "missed" motion in one frame.
        clock.start();
        _rafHandle = requestAnimationFrame(tick);
    }

    // ── Mirror-mode sync ───────────────────────────────────────────────
    // Mirror mode: ONE master WebView2 (primary monitor) broadcasts its
    // camera state to the host every ~100 ms. The host fans it out to
    // every OTHER wallpaper WebView2 (the slaves), which apply the state
    // directly so all monitors stay visually in sync.
    //
    // Trade-offs:
    //   • 10 fps update rate is plenty for the Universe's slow drift /
    //     fly-to motion — human eye doesn't notice the discretization at
    //     this scale.
    //   • All randomness (Random camera mode, drift) runs on the master
    //     and gets pushed to slaves, so slaves don't independently make
    //     different decisions.

    /**
     * Slave-side: apply broadcast state from the master directly. No lerp
     * — we want the next render to show the new pose immediately so the
     * mirror feels "tight" rather than "trailing".
     */
    function applyMirrorState(targetVec, camVec) {
        _mirrorIsSlave = true;
        if (!targetVec || !camVec) return;
        controls.target.set(targetVec.x, targetVec.y, targetVec.z);
        camera.position.set(camVec.x, camVec.y, camVec.z);
        controls.update();
        // Any in-flight flyTo is now stale — kill it so it doesn't
        // re-overwrite our applied pose on the next stepFly tick.
        fly = null;
    }

    /**
     * Master-side: start a 100 ms timer that posts the current camera
     * pose via the supplied `postFn` callback. Caller passes the host
     * bridge wrapper from app.js so scene.js stays platform-agnostic.
     * Calling again replaces the previous interval (no double-fire).
     */
    function startMirrorBroadcast(postFn) {
        stopMirrorBroadcast();
        if (typeof postFn !== 'function') return;
        _mirrorBroadcastTimer = setInterval(() => {
            try {
                postFn({
                    target:   { x: controls.target.x, y: controls.target.y, z: controls.target.z },
                    position: { x: camera.position.x, y: camera.position.y, z: camera.position.z },
                });
            } catch (e) { console.warn('[mirror] broadcast failed', e); }
        }, 100);
    }
    function stopMirrorBroadcast() {
        if (_mirrorBroadcastTimer) {
            clearInterval(_mirrorBroadcastTimer);
            _mirrorBroadcastTimer = null;
        }
    }

    return {
        mount,
        setSize,
        focusNode,
        focusGalaxy,
        resetView,
        resettle,
        firePulse,
        firePulseRandom,
        reorganize,
        setBrainWork,
        applySemanticSprings,
        // Peer-halo API — see addPeer / removePeer / pulsePeerActivity
        // declarations above. Idempotent + safe to call before brain mount.
        addPeer,
        removePeer,
        pulsePeerActivity,
        setMotion,
        setGlow,
        setStars,
        setSky,
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
        fitToScreen,
        getSettings,
        pauseAnimation,
        resumeAnimation,
        applyMirrorState,
        startMirrorBroadcast,
        stopMirrorBroadcast,
        destroy
    };
}

function clamp(v, min, max) { return Math.min(max, Math.max(min, v)); }
