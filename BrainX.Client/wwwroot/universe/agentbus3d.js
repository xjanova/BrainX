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
    // Low, tilted vantage — orbits read as ellipses instead of concentric
    // circles, which is what makes it look like a system and not a dartboard.
    camera.position.set(0, 3.4, 7.4);
    camera.lookAt(0, 0, 0);

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

    let raf = 0, running = false, lastT = 0;

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
        motes.push({ mesh: mote, planet: p, t: 0, dur: inbound ? 0.62 : 0.58, inbound });
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
        motes.forEach(m => disposeDeep(m.mesh));
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

        return {
            pivot, mesh, ring, orbitR: 1, online: false, everSeen: false,
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
        p.mesh.visible = p.everSeen;              // never connected → bare orbit
        p.ring.material.opacity = p.online ? 0.34 : (p.everSeen ? 0.16 : 0.07);
        p.mesh.scale.setScalar(p.online ? 1 : 0.78);
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

        for (const p of planets.values()) {
            p.phase += p.speed * dt * (p.online ? 1 : 0.35);
            p.pivot.position.set(Math.cos(p.phase) * p.orbitR, 0, Math.sin(p.phase) * p.orbitR);
        }

        for (let i = motes.length - 1; i >= 0; i--) {
            const m = motes[i];
            m.t += dt / m.dur;
            if (m.t >= 1) { scene.remove(m.mesh); disposeDeep(m.mesh); motes.splice(i, 1); continue; }
            const from = m.inbound ? m.planet.pivot.position : new THREE.Vector3(0, 0, 0);
            const to   = m.inbound ? new THREE.Vector3(0, 0, 0) : m.planet.pivot.position;
            // Arc the path slightly above the orbital plane so an inbound and
            // an outbound mote on the same spoke never overlap.
            const k = m.t;
            m.mesh.position.lerpVectors(from, to, k);
            m.mesh.position.y += Math.sin(k * Math.PI) * (m.inbound ? 0.42 : -0.34);
            m.mesh.material.opacity = k < 0.15 ? k / 0.15 : (k > 0.85 ? (1 - k) / 0.15 : 1);
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

function disposeDeep(obj) {
    obj.traverse?.((o) => {
        o.geometry?.dispose?.();
        if (Array.isArray(o.material)) o.material.forEach(m => m.dispose?.());
        else o.material?.dispose?.();
    });
    obj.geometry?.dispose?.();
    obj.material?.dispose?.();
}
