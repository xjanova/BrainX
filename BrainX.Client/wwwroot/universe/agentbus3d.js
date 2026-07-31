/* Agent Bus, rendered as a solar system.
 *
 * The brain is the star; every agent connected to it is a planet on its own
 * orbit. Presence is luminosity — an online agent burns, an offline one is a
 * cold rock, one that has never connected is a bare orbit line with nothing on
 * it. Traffic is a comet: a mote leaves the planet, falls into the star
 * (request), and a paler one comes back out (response). That reads as
 * direction without a legend, which the flat 2D card needed.
 *
 * Deliberately its own tiny scene rather than part of the main galaxy: mixing
 * agents into the knowledge graph would imply they ARE nodes, and the panel
 * needs a fixed close-up framing that the user's free camera would fight.
 *
 * Cheap on purpose — a HUD panel must not cost frames. No postprocessing, no
 * shadows, ~12 objects, and the loop parks itself when the panel is off-screen
 * or the tab is hidden.
 */

import * as THREE from 'three';

const AGENT_COLORS = {
    claude:  0xe8825a,
    codex:   0x19a385,
    cluadex: 0x8b7cf6,
    unity:   0xc9cfd6,
    unreal:  0x4fb3e8,
    brain:   0x6fa8ff,
};
const UNKNOWN_COLOR = 0x8e9aa6;

const colorOf = (name) => AGENT_COLORS[String(name).toLowerCase()] ?? UNKNOWN_COLOR;

export function createAgentBus3D(canvas) {
    if (!canvas) return null;

    const renderer = new THREE.WebGLRenderer({ canvas, alpha: true, antialias: true });
    renderer.setClearColor(0x000000, 0);
    // Cap DPR: this panel is ~260 px wide, and rendering it at 3× on a hidpi
    // screen buys nothing visible while tripling its pixel cost.
    renderer.setPixelRatio(Math.min(devicePixelRatio || 1, 1.75));

    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(38, 1, 0.1, 100);

    /* Camera as spherical coordinates around the star, driven by the mouse.
     *
     * Hand-rolled rather than OrbitControls: three.js is vendored here, and
     * pulling in the addon means patching its bare `from 'three'` import (the
     * ATMOS 3D build hit exactly that). This needs two gestures, and two
     * gestures is thirty lines.
     *
     * The default is the old fixed framing — low and tilted, so the orbits
     * read as ellipses instead of concentric circles, which is what makes it
     * look like a system and not a dartboard. */
    const view = {
        theta: 0,                 // around the vertical axis
        phi: 0.43,                // above the orbital plane, radians
        dist: 8.15,               // matches the original (0, 3.4, 7.4)
        target: new THREE.Vector3(0, 0, 0),
    };
    const DIST_MIN = 2.2, DIST_MAX = 16;
    const PHI_MIN = 0.06, PHI_MAX = 1.45;      // never quite top-down or edge-on

    function applyCamera() {
        const r = view.dist, cp = Math.cos(view.phi), sp = Math.sin(view.phi);
        camera.position.set(
            view.target.x + r * cp * Math.sin(view.theta),
            view.target.y + r * sp,
            view.target.z + r * cp * Math.cos(view.theta));
        camera.lookAt(view.target);
    }
    applyCamera();

    scene.add(new THREE.AmbientLight(0xffffff, 0.55));
    const starLight = new THREE.PointLight(0x9ecfff, 26, 40);
    scene.add(starLight);

    // ── The star ────────────────────────────────────────────────
    const star = new THREE.Mesh(
        new THREE.SphereGeometry(0.52, 32, 24),
        new THREE.MeshBasicMaterial({ color: 0xbfe3ff }));
    scene.add(star);

    const corona = new THREE.Sprite(new THREE.SpriteMaterial({
        map: radialTexture(),
        color: 0x6fa8ff,
        blending: THREE.AdditiveBlending,
        depthWrite: false,
        transparent: true,
    }));
    corona.scale.setScalar(3.1);
    scene.add(corona);

    const planets = new Map();   // name → { pivot, mesh, ring, orbitR, speed, phase, online, everSeen }
    const motes = [];            // in-flight traffic
    const moteGeo = new THREE.SphereGeometry(0.075, 8, 8);
    /** Points in a comet tail. Long enough to read as motion, short enough
     *  that a burst of traffic is still a burst and not a cobweb. */
    const TRAIL_LEN = 14;

    /* Name tags. They start appearing when the camera is closer than FAR and
     * are fully solid by NEAR — a fade rather than a switch, so zooming feels
     * like approaching something rather than tripping a sensor. */
    const LABEL_FAR = 6.4, LABEL_NEAR = 4.2;
    const LABEL_W = 1.35, LABEL_H = 0.34;

    let raf = 0, running = false, lastT = 0;

    // ── Mouse: drag to orbit, wheel to zoom ─────────────────────
    //
    // Every handler swallows its event. This canvas sits INSIDE the galaxy's
    // page, and the galaxy listens for drag and wheel too — without this, one
    // gesture would turn the solar system and fly the main camera at the same
    // time, and the panel's own scroller would join in on the wheel.
    let dragging = false, lastX = 0, lastY = 0, userMoved = false;

    canvas.addEventListener('pointerdown', (e) => {
        dragging = true; userMoved = true;
        lastX = e.clientX; lastY = e.clientY;
        canvas.setPointerCapture?.(e.pointerId);
        e.stopPropagation(); e.preventDefault();
    });
    canvas.addEventListener('pointermove', (e) => {
        if (!dragging) return;
        // Scale by canvas size so the same drag turns the same amount whether
        // the panel is 200px or 400px wide.
        const w = canvas.clientWidth || 240;
        view.theta -= ((e.clientX - lastX) / w) * Math.PI * 2;
        view.phi = clamp(view.phi + ((e.clientY - lastY) / w) * Math.PI * 1.4, PHI_MIN, PHI_MAX);
        lastX = e.clientX; lastY = e.clientY;
        applyCamera();
        e.stopPropagation(); e.preventDefault();
    });
    const endDrag = (e) => {
        if (!dragging) return;
        dragging = false;
        canvas.releasePointerCapture?.(e.pointerId);
        e.stopPropagation();
    };
    canvas.addEventListener('pointerup', endDrag);
    canvas.addEventListener('pointercancel', endDrag);
    canvas.addEventListener('wheel', (e) => {
        userMoved = true;
        view.dist = clamp(view.dist * (e.deltaY > 0 ? 1.12 : 0.89), DIST_MIN, DIST_MAX);
        applyCamera();
        e.stopPropagation(); e.preventDefault();
    }, { passive: false });
    // Double-click puts it back — a view you can lose is a view you need a way
    // out of, and "drag until it looks right again" is not one.
    canvas.addEventListener('dblclick', (e) => {
        view.theta = 0; view.phi = 0.43; view.dist = 8.15;
        userMoved = false;
        applyCamera();
        e.stopPropagation(); e.preventDefault();
    });

    // ── Public API ──────────────────────────────────────────────

    /** Rebuild the system. Agents keep their orbital phase across updates so a
     *  presence poll never makes the planets jump. */
    function setAgents(list) {
        let rosterChanged = false;
        const seen = new Set();
        list.forEach((a, i) => {
            const name = a.name;
            seen.add(name);
            let p = planets.get(name);
            if (!p) {
                p = buildPlanet(name, i, list.length);
                planets.set(name, p);
                rosterChanged = true;
            }
            applyPresence(p, a);
        });
        // Drop agents that vanished from the roster.
        for (const [name, p] of planets) {
            if (seen.has(name)) continue;
            scene.remove(p.pivot, p.ring);
            disposeDeep(p.pivot); disposeDeep(p.ring);
            planets.delete(name);
            rosterChanged = true;
        }
        // Orbits depend on nothing but WHO is in the system, and the host
        // re-sends presence every couple of seconds — laying out every time
        // would throw away and rebuild a ring geometry per agent, forever, to
        // arrive at identical numbers. Tracked as "did anyone join or leave"
        // rather than a size comparison, because one agent replacing another
        // leaves the count untouched and the newcomer un-placed.
        if (rosterChanged) layoutOrbits();
    }

    /** One request/response round trip on an agent's orbit. */
    function fireTraffic(name, inbound = true) {
        const p = planets.get(name);
        if (!p) return;
        // Motes are retired by the render loop, and the loop is stopped
        // whenever this canvas is off-screen — so without a ceiling, traffic
        // reported while the panel is scrolled out of view would queue up
        // forever and then all burst at once. 60 is far past what is legible.
        if (motes.length > 60) return;
        const color = inbound ? colorOf(name) : AGENT_COLORS.brain;
        const mote = new THREE.Mesh(moteGeo, new THREE.MeshBasicMaterial({
            color, blending: THREE.AdditiveBlending, transparent: true, depthWrite: false,
        }));
        scene.add(mote);

        // The tail. A message that leaves a streak reads as something
        // TRAVELLING; a bare dot reads as something blinking. It is a line
        // through the last N positions, coloured from the mote's own colour at
        // the head down to black at the tip — additive blending means black is
        // invisible, so the fade needs no per-vertex alpha.
        const trailGeo = new THREE.BufferGeometry();
        trailGeo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(TRAIL_LEN * 3), 3));
        trailGeo.setAttribute('color', new THREE.BufferAttribute(new Float32Array(TRAIL_LEN * 3), 3));
        const trail = new THREE.Line(trailGeo, new THREE.LineBasicMaterial({
            vertexColors: true, blending: THREE.AdditiveBlending,
            transparent: true, depthWrite: false,
        }));
        trail.frustumCulled = false;      // the points are written every frame
        scene.add(trail);

        motes.push({
            mesh: mote, trail, planet: p, t: 0, dur: inbound ? 0.62 : 0.58, inbound,
            head: new THREE.Color(color), history: [],
        });
    }

    function resize() {
        const w = canvas.clientWidth || 240, h = canvas.clientHeight || 150;
        if (w < 4 || h < 4) return;
        renderer.setSize(w, h, false);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
    }

    function start() { if (!running) { running = true; lastT = performance.now(); raf = requestAnimationFrame(tick); } }
    function stop()  { running = false; cancelAnimationFrame(raf); }

    function dispose() {
        stop();
        planets.forEach(p => { disposeDeep(p.pivot); disposeDeep(p.ring); });
        motes.forEach(m => { disposeDeep(m.mesh); disposeDeep(m.trail); });
        disposeDeep(scene);
        renderer.dispose();
    }

    // ── Internals ───────────────────────────────────────────────

    function buildPlanet(name, index, total) {
        const color = colorOf(name);
        const pivot = new THREE.Object3D();
        const mesh = new THREE.Mesh(
            new THREE.SphereGeometry(0.2, 20, 16),
            new THREE.MeshStandardMaterial({ color, emissive: color, emissiveIntensity: 0.9, roughness: 0.55 }));
        pivot.add(mesh);
        scene.add(pivot);

        const ring = new THREE.Line(
            ringGeometry(1),
            new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.2 }));
        ring.rotation.x = Math.PI / 2;
        scene.add(ring);

        // Name tag. A sprite rather than an HTML overlay: it rides the pivot,
        // so it needs no per-frame projection maths, and it is clipped by the
        // canvas for free. Hidden until the camera is close enough for the
        // text to be worth reading — at the default framing five of these
        // would be five smudges over the orbits.
        const label = new THREE.Sprite(new THREE.SpriteMaterial({
            map: labelTexture(name, color),
            transparent: true, depthWrite: false, depthTest: false, opacity: 0,
        }));
        label.scale.set(LABEL_W, LABEL_H, 1);
        label.position.y = 0.42;
        label.visible = false;
        pivot.add(label);

        return {
            pivot, mesh, ring, label, orbitR: 1, online: false, everSeen: false,
            // Inner orbits move faster, like a real system — and it keeps two
            // planets from sitting locked next to each other forever.
            speed: 0.34 - index * 0.045,
            phase: (index / Math.max(1, total)) * Math.PI * 2,
        };
    }

    function applyPresence(p, a) {
        p.online = !!a.online;
        p.everSeen = a.everSeen !== false;
        const color = colorOf(a.name);
        p.mesh.material.color.setHex(p.everSeen ? color : 0x3a3550);
        p.mesh.material.emissive.setHex(p.online ? color : 0x000000);
        p.mesh.material.emissiveIntensity = p.online ? 1.15 : 0;
        // A never-connected agent used to be a bare orbit with no body. But the
        // roster is now a fixed allowlist, so an empty ring reads as "something
        // is broken" rather than "this one is configured and idle" — which is
        // exactly the state unity/unreal sit in until their editor is open.
        // Show it, dark and small; presence is what lights it up.
        p.mesh.visible = true;
        p.ring.material.opacity = p.online ? 0.34 : (p.everSeen ? 0.16 : 0.1);
        p.mesh.scale.setScalar(p.online ? 1 : 0.78);
    }

    /**
     * Push the head position into the tail and rewrite the line.
     *
     * The history is padded to full length from the first frame, so a mote
     * that has only just been fired draws a tail collapsed at its own position
     * rather than a stray segment reaching back to the origin.
     */
    function updateTrail(m, fade) {
        m.history.unshift(m.mesh.position.clone());
        while (m.history.length > TRAIL_LEN) m.history.pop();

        const pos = m.trail.geometry.attributes.position;
        const col = m.trail.geometry.attributes.color;
        for (let i = 0; i < TRAIL_LEN; i++) {
            const p = m.history[Math.min(i, m.history.length - 1)];
            pos.setXYZ(i, p.x, p.y, p.z);
            // Head keeps the mote's colour; the tip decays to black, which is
            // nothing at all under additive blending.
            const t = 1 - i / (TRAIL_LEN - 1);
            const a = t * t * fade;
            col.setXYZ(i, m.head.r * a, m.head.g * a, m.head.b * a);
        }
        pos.needsUpdate = true;
        col.needsUpdate = true;
    }

    function layoutOrbits() {
        const n = planets.size || 1;
        let i = 0;
        for (const p of planets.values()) {
            p.orbitR = 1.5 + i * (2.6 / Math.max(1, n));
            p.ring.geometry.dispose();
            p.ring.geometry = ringGeometry(p.orbitR);
            i++;
        }
    }

    function tick(now) {
        if (!running) return;
        const dt = Math.min(0.05, (now - lastT) / 1000);
        lastT = now;

        // Star breathes so the panel is alive even with zero traffic.
        const pulse = 1 + Math.sin(now / 620) * 0.045;
        star.scale.setScalar(pulse);
        corona.scale.setScalar(3.1 * pulse);

        // Name tags fade in as the camera closes. Computed once, not per
        // planet — they all share the same distance from the star.
        const labelAlpha = clamp((LABEL_FAR - view.dist) / (LABEL_FAR - LABEL_NEAR), 0, 1);

        for (const p of planets.values()) {
            p.phase += p.speed * dt * (p.online ? 1 : 0.35);
            p.pivot.position.set(Math.cos(p.phase) * p.orbitR, 0, Math.sin(p.phase) * p.orbitR);
            if (p.label) {
                // Every node on the allowlist names itself on zoom-in, including
                // ones that have never connected — that IS the answer to "which
                // dark planet is that?", and it is the only place the name
                // appears now that the text roster is gone.
                const a = p.everSeen ? labelAlpha : labelAlpha * 0.55;
                p.label.visible = a > 0.01;
                p.label.material.opacity = a;
            }
        }

        for (let i = motes.length - 1; i >= 0; i--) {
            const m = motes[i];
            m.t += dt / m.dur;
            if (m.t >= 1) {
                scene.remove(m.mesh, m.trail);
                disposeDeep(m.mesh); disposeDeep(m.trail);
                motes.splice(i, 1);
                continue;
            }
            const from = m.inbound ? m.planet.pivot.position : new THREE.Vector3(0, 0, 0);
            const to   = m.inbound ? new THREE.Vector3(0, 0, 0) : m.planet.pivot.position;
            // Arc the path slightly above the orbital plane so an inbound and
            // an outbound mote on the same spoke never overlap.
            const k = m.t;
            m.mesh.position.lerpVectors(from, to, k);
            m.mesh.position.y += Math.sin(k * Math.PI) * (m.inbound ? 0.42 : -0.34);
            const fade = k < 0.15 ? k / 0.15 : (k > 0.85 ? (1 - k) / 0.15 : 1);
            m.mesh.material.opacity = fade;
            updateTrail(m, fade);
        }

        renderer.render(scene, camera);
        raf = requestAnimationFrame(tick);
    }

    resize();
    start();

    /** Orbital positions, for tests and for eyeballing motion from a console.
     *  Returning live data rather than a screenshot is the only way to prove
     *  the loop is turning: WebGL clears its drawing buffer after compositing,
     *  so readPixels on this canvas legitimately returns zeros. */
    function debugState() {
        return {
            running,
            view: { theta: +view.theta.toFixed(3), phi: +view.phi.toFixed(3),
                    dist: +view.dist.toFixed(2), userMoved },
            labelsVisible: [...planets.values()].filter(p => p.label?.visible).length,
            trails: motes.filter(m => m.trail).length,
            planets: [...planets.entries()].map(([name, p]) => ({
                name, online: p.online, r: +p.orbitR.toFixed(2),
                x: +p.pivot.position.x.toFixed(3), z: +p.pivot.position.z.toFixed(3),
            })),
            motes: motes.length,
        };
    }

    return { setAgents, fireTraffic, resize, start, stop, dispose, debugState };
}

// ── helpers ─────────────────────────────────────────────────────

function ringGeometry(r) {
    const pts = [];
    for (let i = 0; i <= 96; i++) {
        const a = (i / 96) * Math.PI * 2;
        pts.push(new THREE.Vector3(Math.cos(a) * r, Math.sin(a) * r, 0));
    }
    return new THREE.BufferGeometry().setFromPoints(pts);
}

/** Soft radial falloff for the corona sprite — generated rather than shipped
 *  so the HUD adds no binary assets. */
function radialTexture() {
    const s = 128;
    const c = document.createElement('canvas');
    c.width = c.height = s;
    const g = c.getContext('2d').createRadialGradient(s / 2, s / 2, 0, s / 2, s / 2, s / 2);
    g.addColorStop(0.00, 'rgba(255,255,255,0.9)');
    g.addColorStop(0.25, 'rgba(160,210,255,0.45)');
    g.addColorStop(1.00, 'rgba(0,0,0,0)');
    const ctx = c.getContext('2d');
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, s, s);
    const tex = new THREE.CanvasTexture(c);
    tex.colorSpace = THREE.SRGBColorSpace;
    return tex;
}

/** A name drawn onto a canvas, for the planet's sprite label. Generated for
 *  the same reason as the corona: the HUD ships no binary assets. */
function labelTexture(name, color) {
    const w = 256, h = 64;
    const c = document.createElement('canvas');
    c.width = w; c.height = h;
    const ctx = c.getContext('2d');
    ctx.font = '600 30px "JetBrains Mono", "Cascadia Mono", Consolas, monospace';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    // Dark halo first: these sit over orbits and a bright star, and unlike the
    // HUD's text they get no panel behind them.
    ctx.lineWidth = 6;
    ctx.strokeStyle = 'rgba(0,0,0,0.85)';
    ctx.strokeText(displayName(name), w / 2, h / 2);
    ctx.fillStyle = '#' + color.toString(16).padStart(6, '0');
    ctx.fillText(displayName(name), w / 2, h / 2);
    const tex = new THREE.CanvasTexture(c);
    tex.colorSpace = THREE.SRGBColorSpace;
    return tex;
}

/** The zoom-in label is now the ONLY place a name is written, so it has to
 *  match how the product spells itself — plain capitalisation gave "Cluadex".
 *  Keep in sync with BusDisplayName in MainWindow.AgentBus.cs. */
const DISPLAY_NAMES = {
    claude: 'Claude', codex: 'Codex', cluadex: 'CluadeX',
    unity: 'Unity', unreal: 'Unreal', brain: 'BrainX',
};

function displayName(name) {
    const s = String(name);
    const known = DISPLAY_NAMES[s.toLowerCase()];
    if (known) return known;
    const pretty = s.charAt(0).toUpperCase() + s.slice(1);
    return pretty.length <= 14 ? pretty : pretty.slice(0, 13) + '…';
}

const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));

function disposeDeep(obj) {
    obj.traverse?.((o) => {
        o.geometry?.dispose?.();
        if (Array.isArray(o.material)) o.material.forEach(m => m.dispose?.());
        // A sprite's canvas texture is not freed by disposing the material.
        o.material?.map?.dispose?.();
        if (!Array.isArray(o.material)) o.material?.dispose?.();
    });
    obj.geometry?.dispose?.();
    obj.material?.map?.dispose?.();
    obj.material?.dispose?.();
}
