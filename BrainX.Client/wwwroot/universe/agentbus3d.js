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
    /* Labels fade in between FAR and NEAR — see the tick loop. These are
     * RATIOS of the fitted distance, not absolute distances, because the fit
     * now depends on the shape of the card (see fitDist). The rule they encode
     * is "the panel shows the names when it opens", and the moment the opening
     * distance became a computed value, two hard-coded numbers could only
     * disagree with it: a squarish card fits at ~7.0 and the old fixed 6.4 had
     * every name faded out before the card was even drawn.
     *
     * The pair reproduces the old hand-tuned framing exactly — at the old 16:9
     * default they resolve to 6.4 and 4.2, giving alpha ≈ 0.55 at rest, which
     * is legible without being right on top of the star. */
    const LABEL_FAR_K = 1.231, LABEL_NEAR_K = 0.808;

    /** Last distance fitDist() computed — the view's "home", which is what the
     *  label fade is measured against even after the owner has zoomed away. */
    let fitted = 5.2;

    /* Only the opening frame, before the first resize computes a real fit. */
    const DEFAULT_DIST = 5.2;

    const view = {
        theta: 0,                 // around the vertical axis
        phi: 0.43,                // above the orbital plane, radians
        dist: DEFAULT_DIST,
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
    /** Ceiling on in-flight motes. Named because fireRelay has to reserve
     *  room for BOTH its legs against the same number — a relay that clears
     *  the check for its first leg and fails it for the second would draw a
     *  message arriving at the brain and never leaving. */
    const MOTE_MAX = 60;
    const moteGeo = new THREE.SphereGeometry(0.115, 10, 10);
    /** Points in a comet tail. Long enough to read as motion, short enough
     *  that a burst of traffic is still a burst and not a cobweb. */
    const TRAIL_LEN = 22;

    /* Name tags. They start appearing when the camera is closer than FAR and
     * are fully solid by NEAR — a fade rather than a switch, so zooming feels
     * like approaching something rather than tripping a sensor. */
    const LABEL_W = 1.35, LABEL_H = 0.34;
    /** Moon orbit radius, in the same units as a planet's 0.2 body. Wide
     *  enough to read as an orbit at the default framing, tight enough that the
     *  pair never looks like two planets sharing a ring. */
    const MOON_R = 0.44;
    const ORIGIN = new THREE.Vector3(0, 0, 0);

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
        view.theta = 0; view.phi = 0.43;
        // Clear the flag BEFORE re-framing: refit() declines to touch a view
        // the owner is holding, and this gesture is the owner letting go.
        userMoved = false;
        refit();
        e.stopPropagation(); e.preventDefault();
    });

    // ── Public API ──────────────────────────────────────────────

    /** Rebuild the system. Agents keep their orbital phase across updates so a
     *  presence poll never makes the planets jump. */
    function setAgents(list) {
        let rosterChanged = false;
        const seen = new Set();
        // Hosts before moons: a moon is parented to its host's pivot, so the
        // host has to exist first, and nothing guarantees the roster arrives in
        // that order.
        const ordered = [...list.filter(a => !a.moonOf), ...list.filter(a => a.moonOf)];
        ordered.forEach((a, i) => {
            const name = a.name;
            seen.add(name);
            let p = planets.get(name);
            if (!p) {
                const host = a.moonOf ? planets.get(a.moonOf) : null;
                // A moon whose host is not on the roster has nothing to orbit.
                // Give it a ring of its own rather than dropping it — a body
                // that exists and is not drawn is the bug this panel keeps
                // having.
                p = host ? buildMoon(name, a.moonOf, host, i)
                         : buildPlanet(name, i, list.length, a.kind);
                planets.set(name, p);
                rosterChanged = true;
            }
            applyPresence(p, a);
        });
        // Drop agents that vanished from the roster — and any moon left
        // orbiting one, whose geometry the host's disposal has just freed.
        for (const [name, p] of planets) {
            const hostGone = p.hostName && !seen.has(p.hostName);
            if (seen.has(name) && !hostGone) continue;
            p.pivot.parent?.remove(p.pivot);
            p.ring.parent?.remove(p.ring);
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
    function fireTraffic(name, inbound = true, forceColor = null) {
        const p = planets.get(name);
        if (!p) return;
        // Motes are retired by the render loop, and the loop is stopped
        // whenever this canvas is off-screen — so without a ceiling, traffic
        // reported while the panel is scrolled out of view would queue up
        // forever and then all burst at once. 60 is far past what is legible.
        if (motes.length >= MOTE_MAX) return;
        // forceColor is how a relayed message keeps ONE identity across both
        // legs: without it the outbound half would repaint itself in the
        // brain's colour and read as a different message.
        const color = forceColor ?? (inbound ? colorOf(name) : AGENT_COLORS.brain);
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

    /**
     * A message from one agent to another, drawn as what it actually is: TWO
     * legs through the star, never a straight line between two planets.
     *
     * The bus is a file mailbox in the vault — codex writes into
     * inbox/claude/, claude reads it out — so nothing ever travels agent to
     * agent. Animating a direct hop would draw a peer-to-peer link that does
     * not exist, and the whole point of this card is that the brain is the
     * middleman. Leg one carries the SENDER's colour inbound; leg two carries
     * the same colour back outbound, so the eye can follow one message across
     * the hand-off instead of seeing two unrelated blips.
     *
     * The second leg is scheduled off a timer rather than chained in the
     * render loop because the loop stops whenever the canvas is off-screen —
     * a queued leg would otherwise fire the moment the panel came back, long
     * after its partner, which reads as traffic that never arrived.
     */
    function fireRelay(fromName, toName) {
        const src = planets.get(fromName);
        const dst = planets.get(toName);
        if (!src && !dst) return;
        // All or nothing. fireTraffic drops anything past MOTE_MAX, so near the
        // ceiling a relay could land its inbound leg and lose the outbound one
        // — drawing a message that went into the brain and never came out,
        // which is the one thing this animation must never claim. Reserve the
        // room for both legs up front, or draw neither.
        const legs = (src ? 1 : 0) + (dst ? 1 : 0);
        if (motes.length + legs > MOTE_MAX) return;
        const color = colorOf(src ? fromName : toName);
        if (src) fireTraffic(fromName, true, color);
        if (!dst) return;
        // Hand-off at the star: leg one's duration, so the outbound mote leaves
        // exactly as the inbound one lands.
        const handoff = src ? 620 : 0;
        setTimeout(() => { if (running) fireTraffic(toName, false, color); }, handoff);
    }

    /** Half-extent of the whole system in world units: the outermost orbit,
     *  plus enough for the planet riding it and its name tag. */
    function systemRadius() {
        let r = 1.5;
        for (const p of planets.values()) if (!p.isMoon) r = Math.max(r, p.orbitR);
        return r + 0.25;
    }

    /**
     * Distance that frames the system for the card's CURRENT shape.
     *
     * The render fills the card now, so the projection has to answer for the
     * card being any rectangle at all. Two constraints, and the camera has to
     * satisfy the harder one:
     *
     *   vertical   — the orbit is a circle of radius R lying flat, seen from
     *                elevation phi, so its apparent half-height is R·sin(phi).
     *                This one is never allowed to overflow: a card that clips
     *                the system top and bottom stops reading as a system.
     *   horizontal — R across, which at the shape this panel shipped with is
     *                already cropped by about a quarter. That crop is not a
     *                bug; it is what makes the thing read as a system you are
     *                inside rather than a diagram you are looking at. 0.72
     *                keeps exactly that framing at the old 16:9 (it lands on
     *                5.21 against the old hand-tuned 5.2) and only pulls the
     *                camera back once a card is narrow enough that the crop
     *                would start eating the orbits themselves.
     *
     * Not applied while the owner is holding the view: a camera that re-frames
     * itself under a hand that just moved it is a camera fighting its user.
     */
    function fitDist() {
        const t = Math.tan(camera.fov * Math.PI / 360);       // tan(half fov)
        const R = systemRadius();
        const vertical = (R * Math.sin(view.phi)) / t * 1.05;
        const horizontal = R / (t * Math.max(0.2, camera.aspect)) * 0.72;
        /* Backing off for a narrow card has to STOP somewhere. Satisfying the
           horizontal constraint outright on a 300×600 card put the camera at
           the 16-unit clamp and the whole system became a speck in the middle
           of it — filling the card with emptiness. Past 1.45× the vertical fit
           the card is simply the wrong shape for this scene, and cropping the
           outer orbit is the better failure: the star and the inner planets
           stay legible. */
        fitted = clamp(Math.min(Math.max(vertical, horizontal), vertical * 1.45),
                       DIST_MIN, DIST_MAX);
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

        // The buffer matches the CSS box exactly — no letterbox, no scaling by
        // the compositor. `false` keeps setSize from writing a style width and
        // height back onto a canvas whose size comes from `inset` in hud.css.
        renderer.setSize(Math.round(w), Math.round(h), false);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
        refit();
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

    function buildPlanet(name, index, total, kind) {
        const color = colorOf(name);
        // A bridge is not one more agent orbiting the brain — it is an engine
        // the brain reaches OUT to, and drawing it as another planet is exactly
        // how Unity read as "an agent that never showed up" for two releases.
        // Faceted body, slow axial spin: machinery, not a client. The orbit
        // itself stays in the XZ plane, because the ring geometry is rotated to
        // match it and tilting one without the other detaches a body from its
        // own orbit.
        const isBridge = kind === 'bridge';
        const pivot = new THREE.Object3D();
        const mesh = new THREE.Mesh(
            isBridge ? new THREE.OctahedronGeometry(0.23)
                     : new THREE.SphereGeometry(0.2, 20, 16),
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
            pivot, mesh, ring, label, orbitR: 1, online: false, everSeen: false, isBridge,
            // World position, refreshed every tick. Motes read THIS, not
            // pivot.position, because a moon's pivot position is local to the
            // planet it hangs off and would send its traffic to the wrong place.
            pos: new THREE.Vector3(),
            // Inner orbits move faster, like a real system — and it keeps two
            // planets from sitting locked next to each other forever.
            speed: 0.34 - index * 0.045,
            phase: (index / Math.max(1, total)) * Math.PI * 2,
        };
    }

    /**
     * A body that orbits another AGENT instead of the brain.
     *
     * Claude Code in local-agent mode is the same product as Claude but a
     * different address on the bus — a message sent to "claude" never reaches
     * it. Folding them into one planet would hide that; a second coral planet
     * made the roster look like it had started growing on its own again, which
     * is the thing the allowlist exists to stop. A moon says both true things:
     * its own body and its own traffic, plainly belonging to what it circles.
     *
     * Everything hangs off the host's pivot, so it follows the host around the
     * star for free and owns no position maths beyond its local orbit.
     */
    function buildMoon(name, hostName, host, index) {
        const color = colorOf(name);
        const pivot = new THREE.Object3D();
        const mesh = new THREE.Mesh(
            new THREE.SphereGeometry(0.085, 14, 10),
            new THREE.MeshStandardMaterial({ color, emissive: color, emissiveIntensity: 0.9, roughness: 0.55 }));
        pivot.add(mesh);
        host.pivot.add(pivot);

        // The orbit ring doubles as the tether — the one line that says these
        // two are one system rather than two dots that happen to be near each
        // other. Parented to the host too, so it travels with it.
        const ring = new THREE.Line(
            ringGeometry(MOON_R),
            new THREE.LineBasicMaterial({ color, transparent: true, opacity: 0.2 }));
        ring.rotation.x = Math.PI / 2;
        host.pivot.add(ring);

        const label = new THREE.Sprite(new THREE.SpriteMaterial({
            map: labelTexture(name, color),
            transparent: true, depthWrite: false, depthTest: false, opacity: 0,
        }));
        label.scale.set(LABEL_W * 0.78, LABEL_H * 0.78, 1);
        label.position.y = 0.26;
        label.visible = false;
        pivot.add(label);

        return {
            pivot, mesh, ring, label, orbitR: MOON_R,
            online: false, everSeen: false, isBridge: false, isMoon: true,
            host, hostName, pos: new THREE.Vector3(),
            // Faster than any planet: a moon drifting at planet speed reads as
            // a second planet that happens to be parked nearby.
            speed: 1.35,
            phase: index * 1.7,
        };
    }

    /* Six states, one word each, decided by the host (BusNodeState in
     * MainWindow.AgentBus.cs) so this file never re-derives the rules and
     * drifts. `ready` is the one that matters most: a bridge that has come up
     * before and will connect on the first call is a WORKING part of the
     * machine, not a dead one, and it spends almost all of its life there —
     * the hub dials the engine lazily so an idle session never pays for a
     * Python process pair per window. */
    const RING_OPACITY = { live: 0.46, ready: 0.22, fault: 0.18, down: 0.16, idle: 0.12, off: 0.06, never: 0.08 };
    const BODY_SCALE = { live: 1.12, ready: 0.86, fault: 0.8, down: 0.76, idle: 0.72, off: 0.62, never: 0.72 };
    /** How hard a body burns. The gap between lit and unlit is deliberately
     *  large — a 15% difference in brightness is a difference nobody sees from
     *  a metre away, and this panel is read at a glance or not at all. */
    const BODY_GLOW = { live: 1.9, fault: 0.55, down: 0.35 };

    function applyPresence(p, a) {
        // Fall back for the browser demo and for any host older than `state`.
        const state = a.state || (a.online ? 'live' : (a.everSeen !== false ? 'idle' : 'never'));
        p.online = state === 'live';
        p.everSeen = a.everSeen !== false;

        const color = colorOf(a.name);
        const dark = state === 'never' || state === 'off';
        p.mesh.material.color.setHex(dark ? 0x3a3550 : color);
        // A failed or unreachable engine SMOULDERS rather than glowing: visibly
        // not connected, visibly not switched off either.
        p.mesh.material.emissive.setHex(
            state === 'live' ? color
            : (state === 'fault' || state === 'down') ? 0x5a1f18
            : 0x000000);
        p.mesh.material.emissiveIntensity = BODY_GLOW[state] ?? 0;

        // A never-connected node used to be a bare orbit with no body. But the
        // roster is a deliberate list, so an empty ring reads as "something is
        // broken" rather than "this one is configured and idle". Show it, dark
        // and small; state is what lights it up.
        p.mesh.visible = true;
        p.ring.material.opacity = RING_OPACITY[state] ?? 0.1;
        p.mesh.scale.setScalar(BODY_SCALE[state] ?? 0.78);
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
        // Moons are not laid out here: their orbit is a fixed radius around a
        // host, not a slot in the system, and rewriting their ring geometry to
        // a star-sized radius would fling them across the panel.
        const primaries = [...planets.values()].filter(p => !p.isMoon);
        const n = primaries.length || 1;
        let i = 0;
        for (const p of primaries) {
            p.orbitR = 1.5 + i * (2.6 / Math.max(1, n));
            p.ring.geometry.dispose();
            p.ring.geometry = ringGeometry(p.orbitR);
            i++;
        }
        // The system just changed size — an agent joining pushes the outermost
        // orbit outward. Re-frame, or the newcomer arrives outside the card.
        refit();
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
        // planet — they all share the same distance from the star. Measured
        // against the fitted "home" distance so the names read the same at
        // every card shape instead of only at the one they were tuned on.
        const far = fitted * LABEL_FAR_K, near = fitted * LABEL_NEAR_K;
        const labelAlpha = clamp((far - view.dist) / (far - near), 0, 1);

        const advance = (p) => {
            p.phase += p.speed * dt * (p.online ? 1 : 0.35);
            p.pivot.position.set(Math.cos(p.phase) * p.orbitR, 0, Math.sin(p.phase) * p.orbitR);
            // Bridges turn on their own axis — an octahedron that never rotates
            // just looks like a badly tessellated planet.
            if (p.isBridge) p.mesh.rotation.y += dt * (p.online ? 1.1 : 0.4);
            if (p.label) {
                // Every node on the allowlist names itself on zoom-in, including
                // ones that have never connected — that IS the answer to "which
                // dark planet is that?", and it is the only place the name
                // appears now that the text roster is gone.
                const a = p.everSeen ? labelAlpha : labelAlpha * 0.55;
                p.label.visible = a > 0.01;
                p.label.material.opacity = a;
            }
        };

        // Hosts first, then moons — a moon's world position is its host's plus
        // its own local orbit, and taking the host's from THIS frame rather
        // than the last one keeps its traffic leaving from where it is drawn.
        for (const p of planets.values()) {
            if (p.isMoon) continue;
            advance(p);
            p.pos.copy(p.pivot.position);
        }
        for (const p of planets.values()) {
            if (!p.isMoon) continue;
            advance(p);
            p.pos.copy(p.host.pos).add(p.pivot.position);
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
            // p.pos, not p.pivot.position: a moon's pivot position is local to
            // the planet it hangs off, so using it would fire the mote from a
            // point half a system away from the body it belongs to.
            const from = m.inbound ? m.planet.pos : ORIGIN;
            const to   = m.inbound ? ORIGIN : m.planet.pos;
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
                x: +p.pos.x.toFixed(3), z: +p.pos.z.toFixed(3),
                moonOf: p.hostName ?? null,
                // Which body was actually built, and how lit it is: the only
                // way to check the bridge branch from a browser without being
                // able to look at the pixels.
                bridge: !!p.isBridge,
                geometry: p.mesh.geometry.type,
                ring: +p.ring.material.opacity.toFixed(2),
                glow: +p.mesh.material.emissiveIntensity.toFixed(2),
            })),
            motes: motes.length,
        };
    }

    return { setAgents, fireTraffic, fireRelay, resize, start, stop, dispose, debugState };
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
    // The bus identity derives from the provenance tag, so an agent running in
    // local-agent mode announces itself as the whole slug. Twenty-nine
    // characters is a planet label nobody can read and a ticker row with no
    // room left for what the agent actually did.
    'local-agent-mode-brainx-brain': 'Local agent',
};

/** Exported so the flow ticker names an agent exactly as its planet does.
 *  Two surfaces deriving the same label separately is how they drift. */
export function displayName(name) {
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
