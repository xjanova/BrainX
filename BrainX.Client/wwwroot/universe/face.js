// face.js — the assistant's face: a deformed quad mesh, and a mouth that
// moves with what she says.
//
// THE MESH IS THE POINT. An earlier version drew sparse contour rings, which
// read as a stack of hoops rather than a face. This builds a parametric
// surface over a (u,v) grid and draws every grid line, so the wires bunch and
// stretch around the brow, the eye sockets, the nose and the mouth — the
// deformation IS the likeness, which is why the grid has to be dense enough
// to bend visibly (26x32) and why the features are cut into the SURFACE
// rather than drawn on top of it.
//
// WHY THE AUDIO IS A FILE AND NOT speechSynthesis. This machine has no Thai
// voice at the OS level (SAPI and WinRT expose David / Zira / Mark, all
// en-US), and the WebView2 reads that same empty list. Thai TTS therefore
// happens server-side via `brainx-mcp speak`, and this module is handed an
// mp3 URL. Nothing here synthesises.
//
// WHY BANDS AND NOT AMPLITUDE. Driving a jaw from loudness gives a puppet
// that flaps with the volume — recognisably wrong, because mouth SHAPE is set
// by formants, not level. Splitting the spectrum into low / mid / high and
// mapping those to mouth HEIGHT vs WIDTH produces genuinely different vowel
// shapes from nothing but an FFT, with no phoneme table and no per-language
// work — which matters when the language is Thai and viseme data is scarce.

import * as THREE from 'three';

const COLS = 26;   // across the face
const ROWS = 32;   // crown to chin

/** Smooth bump, 1 at the centre, 0 by `r`. Used for every facial feature. */
function bump(d, r) {
    if (d >= r) return 0;
    const t = 1 - d / r;
    return t * t * (3 - 2 * t);          // smoothstep, so features blend
}

/**
 * The face surface. Given grid coordinates u (-1 left .. 1 right) and
 * v (-1 chin .. 1 crown), return the 3D point.
 *
 * Feature depths are subtractive or additive bumps on a base dome. Sockets
 * pull the surface IN, the nose and brow push it OUT — the same thing a real
 * face does to a mesh laid over it, which is why the wires read correctly
 * without any texture.
 */
function facePoint(u, v, P, mouth) {
    // Outline: widest at the cheekbones (v ~ 0.1), tapering to the chin and,
    // less sharply, to the crown. Anything wider at the jaw than the cheek
    // reads as masculine, so this curve carries most of the gender.
    const vv = (v - 0.10) / 1.18;
    const halfW = P.width * Math.sqrt(Math.max(0, 1 - vv * vv)) *
                  (v < -0.55 ? 1 - P.jawTaper * ((-0.55 - v) / 0.45) ** 1.6 : 1);
    const x = u * halfW;
    const y = v * P.height;

    // Base dome.
    const rr = Math.min(1, u * u * 0.85 + vv * vv * 0.9);
    let z = P.depth * Math.sqrt(Math.max(0, 1 - rr));

    const ax = Math.abs(x), du = Math.abs(u);

    // Brow ridge — a horizontal swell above the eyes.
    z += 0.085 * bump(Math.hypot((v - 0.30) * 2.6, (du - 0.44) * 1.5), 1);

    // Eye sockets — the deepest recess on a face, and the feature the grid
    // bends around most visibly.
    const eye = bump(Math.hypot((du - 0.44) * 2.3, (v - 0.16) * 2.9), 1);
    z -= 0.185 * eye;

    // Nose: a ridge down the midline that flares into the tip and nostrils.
    const ridge = bump(du * 7.0, 1) * bump(Math.abs(v - 0.02) * 1.9, 1);
    z += 0.105 * ridge;
    z += 0.075 * bump(Math.hypot(du * 5.2, (v + 0.20) * 4.2), 1);   // tip
    z -= 0.045 * bump(Math.hypot((du - 0.14) * 6.5, (v + 0.24) * 6.0), 1); // nostril crease

    // Mouth: a recess that the audio widens and opens. The mesh itself
    // deforms — a separate mouth object floating on the surface is what makes
    // a talking head look pasted together.
    const mw = 0.30 + mouth.wide * 0.13;
    const mh = 0.055 + mouth.open * 0.16;
    const md = bump(Math.hypot((du / mw), ((v + 0.46 + mouth.open * 0.05) / mh)), 1);
    z -= (0.055 + mouth.open * 0.090) * md;
    // Lips part: pull the upper edge up and the lower edge down so the
    // opening is a hole in the grid rather than a dent in it.
    const lip = md * mouth.open * 0.10;
    const yOut = y + (v > -0.46 ? lip : -lip);

    // Chin and cheekbones — small, but they stop the lower face reading flat.
    z += 0.030 * bump(Math.hypot(du * 2.4, (v + 0.80) * 3.0), 1);
    z += 0.022 * bump(Math.hypot((du - 0.62) * 2.2, (v - 0.02) * 2.2), 1);

    return [x, yOut, z];
}

/** Radial-gradient sprite used for the glowing eyes. */
function glowTexture() {
    const c = document.createElement('canvas');
    c.width = c.height = 128;
    const g = c.getContext('2d');
    const grd = g.createRadialGradient(64, 64, 0, 64, 64, 64);
    grd.addColorStop(0.00, 'rgba(190,255,210,1)');
    grd.addColorStop(0.25, 'rgba(60,240,140,0.95)');
    grd.addColorStop(0.55, 'rgba(20,200,110,0.45)');
    grd.addColorStop(1.00, 'rgba(0,120,70,0)');
    g.fillStyle = grd;
    g.fillRect(0, 0, 128, 128);
    return new THREE.CanvasTexture(c);
}

function buildHead(female) {
    const g = new THREE.Group();

    // Proportions carry the gender; the feature maths above is shared.
    const P = female
        ? { width: 0.86, height: 1.16, depth: 0.62, jawTaper: 0.34 }
        : { width: 0.98, height: 1.18, depth: 0.66, jawTaper: 0.18 };

    const N = COLS * ROWS;
    const pos = new Float32Array(N * 3);

    // One geometry, drawn as LineSegments: every horizontal and vertical grid
    // edge. Cheaper and crisper than a Line per row, and it is what gives the
    // even mesh of the reference rather than a set of independent curves.
    const idx = [];
    const at = (c, r) => r * COLS + c;
    for (let r = 0; r < ROWS; r++)
        for (let c = 0; c < COLS; c++) {
            if (c < COLS - 1) idx.push(at(c, r), at(c + 1, r));
            if (r < ROWS - 1) idx.push(at(c, r), at(c, r + 1));
        }

    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
    geo.setIndex(idx);

    const mat = new THREE.LineBasicMaterial({
        color: 0x35e08a, transparent: true, opacity: 0.72,
    });
    const mesh = new THREE.LineSegments(geo, mat);
    g.add(mesh);

    // Eyes: additive glow sprites sitting just inside the sockets, so the
    // light reads as coming from within the mesh rather than floating on it.
    const tex = glowTexture();
    const eyes = [];
    for (const sx of [-1, 1]) {
        const s = new THREE.Sprite(new THREE.SpriteMaterial({
            map: tex, color: 0x4bff9b, transparent: true,
            blending: THREE.AdditiveBlending, depthWrite: false, opacity: 0.95,
        }));
        s.scale.set(0.40, 0.26, 1);
        const [ex, ey, ez] = facePoint(sx * 0.44, 0.16, P, { open: 0, wide: 0 });
        s.position.set(ex, ey, ez + 0.06);
        g.add(s); eyes.push(s);
    }

    return { group: g, mesh, geo, pos, P, eyes };
}

/**
 * Emotions as four continuous dials rather than a set of poses, so they
 * blend: a face can be 60% pleased and still opening its mouth to speak,
 * which a swap-the-pose approach cannot do.
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

export class AssistantFace {
    constructor(host, female = true) {
        this.host = host;
        this.female = female;
        this.speaking = false;
        this._open = 0;
        this._wide = 0;
        this._blinkAt = 0;
        this.mood = { ...MOODS.neutral };
        this._mood = { ...MOODS.neutral };

        const w = host.clientWidth || 260, h = host.clientHeight || 320;
        this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
        this.renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
        this.renderer.setSize(w, h, false);
        host.appendChild(this.renderer.domElement);

        this.scene = new THREE.Scene();
        this.camera = new THREE.PerspectiveCamera(32, w / h, 0.1, 100);
        this.camera.position.set(0, 0, 4.6);

        this.head = buildHead(female);
        this.scene.add(this.head.group);
        this._rebuild(0, 0);

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
        this.head.geo.dispose();
        this.head.mesh.material.dispose();
        this.head = buildHead(female);
        this.scene.add(this.head.group);
        this._rebuild(this._open, this._wide);
    }

    /** Recompute every grid vertex for the current mouth shape. */
    _rebuild(open, wide) {
        const H = this.head, m = { open, wide };
        let i = 0;
        for (let r = 0; r < ROWS; r++) {
            const v = 1 - (r / (ROWS - 1)) * 2;
            for (let c = 0; c < COLS; c++) {
                const u = (c / (COLS - 1)) * 2 - 1;
                const [x, y, z] = facePoint(u, v, H.P, m);
                H.pos[i++] = x; H.pos[i++] = y; H.pos[i++] = z;
            }
        }
        H.geo.attributes.position.needsUpdate = true;
        H.geo.computeBoundingSphere();
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

        for (const k of Object.keys(this._mood))
            this._mood[k] += ((this.mood[k] ?? 0) - this._mood[k]) * 0.08;
        const M = this._mood;

        let open = 0, wide = 0;
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
        // rates read as mush.
        const po = this._open, pw = this._wide;
        // Attack near-instant, release merely quick. A mouth that closes as
        // fast as it opens chatters between syllables; one that opens slowly
        // is always a beat behind the word.
        this._open += (open - this._open) * (open > this._open ? 0.90 : 0.38);
        this._wide += (wide - this._wide) * 0.34;
        // Only rebuild when the shape actually moved — this is 832 vertices of
        // trigonometry and it does not need to run on a still face.
        if (Math.abs(this._open - po) > 0.0012 || Math.abs(this._wide - pw) > 0.0012)
            this._rebuild(this._open, this._wide);

        // Eyes: mood sets the aperture, blinks scale it to nothing. A face
        // that never blinks is unsettling in a way people notice without
        // being able to say why.
        if (t > this._blinkAt) { this._blinkAt = t + 2200 + Math.random() * 3200; this._blinkStart = t; }
        const bp = (t - (this._blinkStart ?? -1e9)) / 130;
        const blink = bp >= 0 && bp <= 1 ? Math.abs(Math.sin(bp * Math.PI)) : 0;
        for (const e of H.eyes) {
            e.scale.y = Math.max(0.02, 0.26 * (M.eyeOpen - blink));
            e.scale.x = 0.40 * (0.9 + M.browRaise * 0.12);
            e.material.opacity = 0.55 + 0.45 * Math.max(0, M.eyeOpen - blink);
        }

        const s = t / 1000;
        H.group.rotation.y = Math.sin(s * 0.4) * 0.15;
        H.group.rotation.x = Math.sin(s * 0.31) * 0.05 + (this.speaking ? 0.03 : 0);
        // The cocked head is half of what makes "thinking" read as thinking
        // rather than as a blank stare.
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
        this.head.geo.dispose();
        this.renderer.dispose();
        this.renderer.domElement.remove();
    }
}
