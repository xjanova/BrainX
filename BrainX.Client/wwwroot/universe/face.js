// face.js — the assistant's wireframe head, and the mouth that moves with her.
//
// WHY THE AUDIO IS A FILE AND NOT speechSynthesis. This machine has no Thai
// voice installed at the OS level (SAPI and WinRT expose David / Zira / Mark,
// all en-US), and the WebView2 reads that same empty list. So Thai TTS happens
// server-side via `brainx-mcp speak`, and this module is handed an mp3 URL.
// Everything here is playback + analysis; nothing here synthesises.
//
// WHY BANDS AND NOT AMPLITUDE. Driving a jaw from overall loudness gives a
// puppet that flaps in time with the volume — recognisably wrong, because
// human mouth SHAPE is set by formants, not level. Splitting the spectrum into
// low / mid / high and mapping those to mouth HEIGHT vs WIDTH gets visibly
// different "ah" / "ee" / "oo" shapes out of nothing but an FFT, with no
// phoneme data and no per-language work — which matters when the language is
// Thai and viseme tables are scarce.
//
// The head follows the VOICE's gender, never a separate toggle: a feminine
// voice over a masculine wireframe reads as a bug, not a style.

import * as THREE from 'three';

const LOW = [0, 8], MID = [8, 40], HIGH = [40, 120];   // FFT bin ranges

/** Average magnitude over a bin range, 0..1. */
function band(data, [a, b]) {
    let s = 0;
    for (let i = a; i < b && i < data.length; i++) s += data[i];
    return s / Math.max(1, Math.min(b, data.length) - a) / 255;
}

/**
 * Build a head as a set of horizontal contour rings plus feature curves —
 * an outline, not a solid. A wireframe SPHERE would read as a planet, and this
 * scene is already full of planets; a face has to be recognisable at a glance
 * in the corner of a busy HUD.
 */
function buildHead(female) {
    const g = new THREE.Group();

    // Jaw width and cheek taper are the two proportions that actually carry
    // the read at this size. Everything else is shared.
    const jawW = female ? 0.72 : 0.88;
    const chinY = female ? -1.02 : -1.08;
    const browY = female ? 0.46 : 0.42;

    const mat = (o = 1) => new THREE.LineBasicMaterial({
        color: 0x67e8f9, transparent: true, opacity: o,
    });

    // ── skull contours: stacked ellipses, narrowing toward the chin ────────
    const rings = new THREE.Group();
    for (let i = 0; i <= 10; i++) {
        const t = i / 10;                       // 0 = crown, 1 = chin
        const y = 0.95 - t * (0.95 - chinY);
        // Widest at the cheekbones (t~0.45), tapering both ways.
        const taper = Math.sin(Math.min(1, t * 1.25) * Math.PI * 0.85);
        const rx = (0.30 + 0.62 * taper) * jawW;
        const rz = (0.26 + 0.50 * taper);
        const pts = [];
        for (let a = 0; a <= 48; a++) {
            const th = (a / 48) * Math.PI * 2;
            pts.push(new THREE.Vector3(Math.cos(th) * rx, y, Math.sin(th) * rz));
        }
        rings.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts),
                                 mat(0.18 + 0.30 * (1 - Math.abs(t - 0.45) * 1.6))));
    }
    g.add(rings);

    // ── vertical profile lines, so it reads as a head and not a stack ─────
    for (const off of [-0.62, -0.3, 0, 0.3, 0.62]) {
        const pts = [];
        for (let i = 0; i <= 20; i++) {
            const t = i / 20;
            const y = 0.95 - t * (0.95 - chinY);
            const taper = Math.sin(Math.min(1, t * 1.25) * Math.PI * 0.85);
            const rx = (0.30 + 0.62 * taper) * jawW;
            const rz = (0.26 + 0.50 * taper);
            const a = off * Math.PI;
            pts.push(new THREE.Vector3(Math.cos(a) * rx, y, Math.sin(a) * rz));
        }
        g.add(new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), mat(0.22)));
    }

    // ── eyes: two rings that blink by scaling to zero ─────────────────────
    const eyes = [];
    for (const sx of [-1, 1]) {
        const pts = [];
        for (let a = 0; a <= 24; a++) {
            const th = (a / 24) * Math.PI * 2;
            pts.push(new THREE.Vector3(Math.cos(th) * 0.13, Math.sin(th) * 0.085, 0));
        }
        const e = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), mat(0.95));
        e.position.set(sx * 0.30 * jawW, 0.16, 0.46);
        g.add(e); eyes.push(e);
    }

    // Brows — a couple of strokes, but they carry more emotion than the rest of
    // the face combined. Kept as objects so the loop can tilt and raise them:
    // inner-end DOWN reads stern/focused, inner-end UP reads worried, both up
    // reads surprised. That single axis is most of what "showing emotion"
    // needs at this size.
    const brows = [];
    for (const sx of [-1, 1]) {
        const pts = [];
        for (let i = 0; i <= 8; i++) {
            const t = i / 8;
            pts.push(new THREE.Vector3((-0.16 + t * 0.32), Math.sin(t * Math.PI) * 0.035, 0));
        }
        const b = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), mat(0.55));
        b.position.set(sx * 0.30 * jawW, browY - 0.12, 0.45);
        b.userData.sx = sx;
        b.userData.baseY = browY - 0.12;
        g.add(b); brows.push(b);
    }

    // ── mouth: a closed loop rebuilt every frame from the audio ───────────
    const mouthGeo = new THREE.BufferGeometry();
    const MOUTH_PTS = 40;
    mouthGeo.setAttribute('position',
        new THREE.BufferAttribute(new Float32Array((MOUTH_PTS + 1) * 3), 3));
    const mouth = new THREE.Line(mouthGeo, mat(1.0));
    mouth.position.set(0, -0.52, 0.44);
    g.add(mouth);

    return { group: g, eyes, brows, mouth, mouthGeo, MOUTH_PTS, jawW };
}

/**
 * Emotions as four continuous dials rather than a set of poses, so they blend:
 * a face can be 60% pleased and still opening its mouth to speak, which a
 * swap-the-sprite approach cannot do.
 *
 *   smile     -1 frown .. +1 smile   (mouth corner lift)
 *   browTilt  -1 stern .. +1 worried (inner brow end down / up)
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

export class AssistantFace {
    /**
     * @param {HTMLElement} host   element to mount the canvas into
     * @param {boolean}     female which head to build (follows the voice)
     */
    constructor(host, female = true) {
        this.host = host;
        this.female = female;
        this.speaking = false;
        this._open = 0;      // smoothed mouth opening
        this._wide = 0;      // smoothed mouth width
        this._raf = null;
        this._blinkAt = 0;

        const w = host.clientWidth || 220, h = host.clientHeight || 260;
        this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
        this.renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
        this.renderer.setSize(w, h, false);
        host.appendChild(this.renderer.domElement);

        this.scene = new THREE.Scene();
        this.camera = new THREE.PerspectiveCamera(34, w / h, 0.1, 100);
        this.camera.position.set(0, -0.05, 4.3);

        this.head = buildHead(female);
        this.scene.add(this.head.group);

        // Current vs target, lerped every frame. Snapping between moods looks
        // mechanical; the travel time is most of what sells it as a face.
        this.mood = { ...MOODS.neutral };
        this._mood = { ...MOODS.neutral };

        this._loop = this._loop.bind(this);
        this._raf = requestAnimationFrame(this._loop);
    }

    /**
     * @param {string|object} m  a MOODS key, or partial dials to blend toward.
     * Unknown names fall back to neutral rather than throwing — a caller that
     * invents a mood should get a calm face, not a broken one.
     */
    setMood(m) {
        const target = typeof m === 'string' ? (MOODS[m] ?? MOODS.neutral) : m;
        this.mood = { ...this.mood, ...target };
        return this;
    }

    /** Rebuild for the other gender without tearing down the canvas. */
    setFemale(female) {
        if (female === this.female) return;
        this.female = female;
        this.scene.remove(this.head.group);
        this.head.group.traverse(o => { o.geometry?.dispose?.(); o.material?.dispose?.(); });
        this.head = buildHead(female);
        this.scene.add(this.head.group);
    }

    /**
     * Play an mp3 and drive the mouth from it. Returns a promise that settles
     * when playback ends.
     *
     * The AudioContext is created on this call, not in the constructor:
     * browsers refuse to start one outside a user gesture, and a context
     * created at load time arrives already suspended and silently stays that
     * way — audible as "the face never moves" with no error anywhere.
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
        an.fftSize = 512;
        an.smoothingTimeConstant = 0.55;
        src.connect(an); an.connect(this.ctx.destination);
        this.analyser = an;
        this.freq = new Uint8Array(an.frequencyBinCount);

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

        let open = 0, wide = 0;
        if (this.speaking && this.analyser) {
            this.analyser.getByteFrequencyData(this.freq);
            const lo = band(this.freq, LOW), mid = band(this.freq, MID), hi = band(this.freq, HIGH);
            // Low energy opens the jaw ("ah"); high energy spreads the lips
            // ("ee"); a low-heavy frame with little high rounds them ("oo").
            open = Math.min(1, lo * 1.9 + mid * 0.7);
            wide = Math.min(1, hi * 2.2 + mid * 0.6 - lo * 0.5);
        }
        // Asymmetric smoothing: mouths open faster than they close, and equal
        // rates read as mush.
        this._open += (open - this._open) * (open > this._open ? 0.55 : 0.22);
        this._wide += (wide - this._wide) * 0.28;

        // Ease the mood dials toward their target.
        for (const k of Object.keys(this._mood))
            this._mood[k] += ((this.mood[k] ?? 0) - this._mood[k]) * 0.08;
        const M = this._mood;

        // Rebuild the mouth loop: an ellipse whose height is the opening and
        // whose width spreads with the high band, plus a corner LIFT from the
        // mood. The lift is a cos² term so it acts on the corners and leaves
        // the centre alone — apply it uniformly and a smile just slides the
        // whole mouth up the face.
        const pos = H.mouthGeo.attributes.position;
        const rx = (0.16 + this._wide * 0.13 + Math.max(0, M.smile) * 0.05) * H.jawW;
        const ry = 0.012 + this._open * 0.20;
        for (let i = 0; i <= H.MOUTH_PTS; i++) {
            const a = (i / H.MOUTH_PTS) * Math.PI * 2;
            const ca = Math.cos(a);
            const x = ca * rx;
            const lift = M.smile * 0.075 * (ca * ca) * Math.sign(ca === 0 ? 1 : 1);
            const y = Math.sin(a) * ry - 0.02 * (1 - this._open) + lift;
            pos.setXYZ(i, x, y, ca * 0.02);
        }
        pos.needsUpdate = true;
        H.mouthGeo.computeBoundingSphere();

        // Brows: raise both, and tilt the INNER ends. Mirrored via userData.sx
        // so the pair stays symmetric about the nose.
        for (const b of H.brows) {
            b.position.y = b.userData.baseY + M.browRaise * 0.07;
            b.rotation.z = -b.userData.sx * M.browTilt * 0.42;
        }

        // Blink on a random-ish cadence; a face that never blinks is unsettling
        // in a way people notice without being able to say why.
        if (t > this._blinkAt) {
            this._blinkAt = t + 2200 + Math.random() * 3200;
            this._blinkStart = t;
        }
        const bp = (t - (this._blinkStart ?? -1e9)) / 130;
        const blink = bp >= 0 && bp <= 1 ? Math.abs(Math.sin(bp * Math.PI)) : 0;
        for (const e of H.eyes) e.scale.y = Math.max(0.05, M.eyeOpen - blink);

        // Idle drift, a lean toward the viewer while speaking, and a head TILT
        // that comes from the mood — the cocked head is half of what makes
        // "thinking" read as thinking rather than as a blank stare.
        const s = t / 1000;
        H.group.rotation.y = Math.sin(s * 0.4) * 0.16;
        H.group.rotation.x = Math.sin(s * 0.31) * 0.06 + (this.speaking ? 0.03 : 0);
        H.group.rotation.z = -M.browTilt * 0.05 + (M.eyeOpen < 0.85 ? 0.06 : 0);

        this.renderer.render(this.scene, this.camera);
    }

    resize(w, h) {
        this.camera.aspect = w / h;
        this.camera.updateProjectionMatrix();
        this.renderer.setSize(w, h, false);
    }

    dispose() {
        cancelAnimationFrame(this._raf);
        this.stop();
        try { this.ctx?.close(); } catch {}
        this.renderer.dispose();
        this.renderer.domElement.remove();
    }
}
