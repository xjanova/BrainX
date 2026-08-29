// avatar.js — Mind as a whole body. Replaces AssistantFace, and keeps its
// public shape (`speak`, `setMood`, `resize`, `dispose`) so the window that
// hosts her does not have to care which one it got.
//
// WHAT THIS FILE ACTUALLY DOES is arbitrate. Five things want to write her
// bones and her face every frame — the animation mixer, the procedural idle,
// the lip sync, the look-at, and the spring bones — and the only reason it
// works is that they run in a fixed order with explicit weights. Get the order
// wrong and one silently erases another with no error to show for it:
//
//   1. mixer      the clip, if any, writes every bone it owns
//   2. idle       breathing / fidget / nods, SLERPED over the top by a weight
//                 that drops to a whisper while a clip is playing
//   3. expressions viseme weights, then mood, then blink
//   4. vrm.update propagates the normalized rig to the real one, runs look-at
//                 and the spring bones LAST, so hair reacts to the final pose
//
// WHY THE CAMERA CHANGES SHOT. A fixed wide shot wastes the expression work —
// while she talks her face is forty pixels tall. A fixed close shot throws away
// the body. So the shot follows the state: bust while speaking, full while
// idle. See framing.js for why the distances are measured rather than typed.

import * as THREE from 'three';
import { GLTFLoader } from './vendor/three/jsm/loaders/GLTFLoader.js';
import { VRMLoaderPlugin, VRMUtils } from '@pixiv/three-vrm';
import { Motion } from './motion.js';
import { Idle } from './idle.js';
import { LipSync } from './lipsync.js';
import { Framing } from './framing.js';

/**
 * Mood -> the VRM expression that carries it, and how much of it.
 *
 * Two moods share an expression on purpose — `pleased` is `happy` turned down.
 * That sharing is also why the expressions are driven from a UNIQUE set below
 * rather than by iterating this table: iterating it sets `happy` twice, and the
 * second pass (for whichever mood is not current) drives it straight back to
 * zero. The face just never smiled, with nothing to show why.
 */
const MOOD_EXPRESSION = {
    neutral:   null,
    happy:     ['happy', 1.00],
    pleased:   ['happy', 0.45],
    concerned: ['sad', 0.55],
    thinking:  null,
    sorry:     ['sad', 0.85],
    alert:     ['surprised', 0.80],
    angry:     ['angry', 0.85],
};

/** Every expression any mood can use, each named once. */
const MOOD_EXPRS = [...new Set(
    Object.values(MOOD_EXPRESSION).filter(Boolean).map(([e]) => e))];

export class Avatar {
    constructor(host, opts = {}) {
        this.host = host;
        this.base = opts.base ?? './avatar/';
        this.model = opts.model ?? 'minde.vrm';
        this.mood = 'neutral';
        this.speaking = false;
        this.ready = false;

        const w = host.clientWidth || 320, h = host.clientHeight || 300;
        this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
        this.renderer.setPixelRatio(Math.min(2, window.devicePixelRatio || 1));
        // updateStyle ON: with it off the canvas gets no CSS size and lays out
        // at its BUFFER size, so on a HiDPI screen she bursts out of the panel
        // and how big she looks depends on the monitor.
        this.renderer.setSize(w, h);
        host.appendChild(this.renderer.domElement);

        this.scene = new THREE.Scene();
        this.camera = new THREE.PerspectiveCamera(28, w / h, 0.1, 40);
        // Toon shading needs a key light with a direction or she reads flat.
        const key = new THREE.DirectionalLight(0xffffff, 2.1);
        key.position.set(1.2, 2.0, 2.4);
        this.scene.add(key, new THREE.AmbientLight(0xbfd4ff, 1.15));

        this.lip = new LipSync();
        this.clock = new THREE.Clock();
        this._loop = this._loop.bind(this);
        this._t = 0;

        this.loaded = this._load();
    }

    async _load() {
        const loader = new GLTFLoader();
        loader.register((p) => new VRMLoaderPlugin(p));
        const gltf = await loader.loadAsync(this.base + this.model);
        const vrm = gltf.userData.vrm;
        // Drop vertices nothing references, and merge the per-primitive
        // skeletons VRoid emits — 20-odd draw calls become a handful.
        for (const fn of ['removeUnnecessaryVertices', 'combineSkeletons'])
            if (typeof VRMUtils[fn] === 'function') { try { VRMUtils[fn](gltf.scene); } catch {} }
        this.vrm = vrm;
        this.scene.add(vrm.scene);

        this.idle = new Idle(vrm);
        this.framing = new Framing(this.camera, vrm);
        this.motion = new Motion(vrm, this.base);

        // She is on screen and breathing before the clips finish arriving:
        // twenty FBX files parse on the main thread and take a couple of
        // seconds, and a blank panel for those seconds is worse than a still
        // one.
        this.ready = true;
        this._raf = requestAnimationFrame(this._loop);

        await this.motion.load();
        this._applyMood();
        return this;
    }

    /** @param {string} m a key of MOOD_EXPRESSION */
    setMood(m) {
        this.mood = m in MOOD_EXPRESSION ? m : 'neutral';
        this._applyMood();
        return this;
    }

    _applyMood() {
        this.motion?.setMood(this.mood);
    }

    /**
     * Say something. Drives the visemes from the audio, pushes in to the bust
     * shot, holds the gesture scheduler off, and puts everything back after.
     */
    async speak(url) {
        this.speaking = true;
        this.motion?.setBusy(true);
        this.framing?.set('bust');
        try {
            return await this.lip.play(url);
        } finally {
            this.speaking = false;
            this.motion?.setBusy(false);
            this.framing?.set('full');
        }
    }

    stop() {
        this.lip.stop();
        this.speaking = false;
        this.motion?.setBusy(false);
        this.framing?.set('full');
    }

    /** Sit down / stand up, if the clips for it are present. */
    sit() { return this.motion?.sit() ?? false; }
    stand() { return this.motion?.stand() ?? false; }

    _loop() {
        this._raf = requestAnimationFrame(this._loop);
        const dt = Math.min(0.1, this.clock.getDelta());
        this._t += dt;
        const vrm = this.vrm;
        if (!vrm) return;

        // 1 — the clip writes the bones it owns.
        this.motion?.update(dt, performance.now());

        // 2 — the procedural layer rides over it. Full weight when no clip has
        // the body; a whisper of breathing when one does, so a canned loop
        // still looks like it is being performed by something that breathes.
        const w = this.motion?.ready ? 0.18 : 1;
        // Mixamo's standing idles are braced, knees bent, weight forward. While
        // she is on her FEET that stance is pulled back toward standing; while
        // she is sitting or mid-transition it is left alone, or the correction
        // would stand her up through the chair.
        const onFeet = !this.motion?.ready
            || ['idle', 'gesture'].includes(this.motion.current?.meta.role ?? 'idle');
        this.lip.update(dt);
        this.idle.apply(this._t, w, this.lip.speaking ? this.lip.level : 0, dt,
                        onFeet ? 0.82 : 0);

        // 3 — face.
        this._face(dt);

        // 4 — propagate, look-at, spring bones.
        vrm.update(dt);

        this.framing?.update(dt, this.speaking ? 0.65 : 1.1);
        this.renderer.render(this.scene, this.camera);
    }

    /**
     * Visemes first, then the mood underneath them, then the blink last so it
     * wins: a blink that loses to an expression is a character who stares
     * through whole sentences.
     *
     * Separate from the render loop so it can be stepped and checked without
     * waiting on requestAnimationFrame, which the host pane throttles to a
     * crawl whenever it is not visible — every timing-based check of this has
     * to be synchronous or it measures the scheduler instead of the code.
     */
    _face(dt = 1 / 60) {
        const em = this.vrm?.expressionManager;
        if (!em) return;
        this.lip.applyTo(this.vrm);
        const [wantExpr, wantAmt] = MOOD_EXPRESSION[this.mood] ?? [null, 0];
        // A VRoid `happy` closes the eyes and reshapes the mouth, so at full
        // weight it simply eats the visemes. Held back while she is talking —
        // the mood stays legible and the words still land.
        const cap = this.speaking ? 0.45 : 1;
        const k = 1 - Math.exp(-5.2 * dt);
        for (const expr of MOOD_EXPRS) {
            const cur = em.getValue(expr) ?? 0;
            const want = expr === wantExpr ? wantAmt * cap : 0;
            em.setValue(expr, cur + (want - cur) * k);
        }
        em.setValue('blink', this.idle.blink);
    }

    resize(w, h) {
        this.camera.aspect = w / h;
        this.camera.updateProjectionMatrix();
        this.renderer.setSize(w, h);
    }

    dispose() {
        cancelAnimationFrame(this._raf);
        this.lip.dispose();
        this.motion?.dispose();
        if (this.vrm) VRMUtils.deepDispose?.(this.vrm.scene);
        this.renderer.dispose();
        this.renderer.domElement.remove();
    }
}
