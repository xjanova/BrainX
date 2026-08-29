// motion.js — what she does with her body: which clip is playing, how one
// hands over to the next, and what she gets up to when nobody is talking to her.
//
// WHY A MANIFEST AND NOT IMPORTS. Motions are data, not code. `clips.json`
// lists everything she could do; anything whose file is missing is skipped with
// a note rather than throwing, so the manifest can describe the whole wish-list
// while only part of it exists on disk. Adding a motion later is a file and a
// line — no build, no code change, and nothing to rewire.
//
// WHY IT RUNS WITH NOTHING LOADED. A system that only works once every asset is
// present cannot be finished or tested until the last file arrives. This one
// reports an empty set and does nothing, and lights up clip by clip as they
// turn up.
//
// WHY THE IDLE IS A LOOP AND EVERYTHING ELSE VISITS. A character that plays one
// clip per state reads as a kiosk. Real idleness is a resting loop with things
// happening ON TOP of it — a glance, a stretch, a wave — that return you to
// where you were. So gestures are one-shots layered over whichever idle is
// current, and the scheduler decides when she feels like one.

import * as THREE from 'three';
import { loadMixamo } from './vendor/vrm-mixamo/retarget.js';

/** Crossfade lengths. Gestures come in fast and leave gently. */
const FADE_IDLE = 0.45;
const FADE_IN = 0.22;
const FADE_OUT = 0.35;

export class Motion {
    /**
     * @param {object} vrm  the loaded VRM (gltf.userData.vrm)
     * @param {string} base directory holding clips.json and the .fbx files
     */
    constructor(vrm, base = './avatar/') {
        this.vrm = vrm;
        this.base = base.endsWith('/') ? base : base + '/';
        this.mixer = new THREE.AnimationMixer(vrm.scene);
        this.clips = new Map();       // id -> {action, meta}
        this.current = null;          // the idle or pose currently held
        this.gesture = null;          // a one-shot playing over it
        this.mood = 'neutral';
        this.missing = [];
        this._nextAt = 0;
        this._busy = false;           // true while she is speaking or working

        // A gesture has to hand back to the idle when it ends. Listening for
        // the mixer's own event is the only way to catch a clip finishing that
        // does not involve polling its time every frame and guessing.
        this.mixer.addEventListener('finished', (e) => {
            if (this.gesture && e.action === this.gesture.action) {
                this.gesture = null;
                if (this._after) { const a = this._after; this._after = null; this.play(a); }
                else this._restoreIdle();
            }
        });
    }

    /** Read the manifest and retarget everything it can find. */
    async load() {
        let manifest;
        try {
            const res = await fetch(this.base + 'clips.json');
            if (!res.ok) throw new Error('HTTP ' + res.status);
            manifest = await res.json();
        } catch (e) {
            console.warn('[motion] no clips.json —', e.message);
            return this;
        }

        // Sequential, not parallel. FBXLoader parses on the main thread, and
        // twenty of them at once stalls the first frame for seconds; one at a
        // time lets her stand there breathing while the rest arrive.
        for (const meta of manifest.clips ?? []) {
            let clip = null;
            try {
                clip = await loadMixamo(this.base + encodeURIComponent(meta.file),
                                        this.vrm, { name: meta.id, quiet: true });
            } catch { /* falls through to `missing` */ }
            if (!clip) { this.missing.push(meta.file); continue; }

            const action = this.mixer.clipAction(clip);
            const oneShot = meta.role === 'gesture' || meta.role === 'transition';
            action.setLoop(oneShot ? THREE.LoopOnce : THREE.LoopRepeat, Infinity);
            action.clampWhenFinished = oneShot;
            this.clips.set(meta.id, { action, meta });
        }

        if (this.missing.length)
            console.info(`[motion] ${this.clips.size} loaded, ${this.missing.length} not present:`,
                         this.missing.join(', '));
        this._restoreIdle();
        return this;
    }

    get ready() { return this.clips.size > 0; }
    get names() { return [...this.clips.keys()]; }

    /** Hold a looping clip: an idle, or a pose like sitting or walking. */
    play(id, fade = FADE_IDLE) {
        const next = this.clips.get(id);
        if (!next || next === this.current) return false;
        next.action.reset().setEffectiveWeight(1).fadeIn(fade).play();
        if (this.current) this.current.action.fadeOut(fade);
        this.current = next;
        return true;
    }

    /**
     * Play a one-shot over whatever is held, then come back to it. `then`
     * names a clip to hold afterwards instead — that is how a transition
     * works: sit_down runs once and leaves her in sit.
     */
    once(id, then = null) {
        const g = this.clips.get(id);
        if (!g) return false;
        if (this.gesture) this.gesture.action.fadeOut(FADE_OUT);
        g.action.reset().setEffectiveWeight(1).fadeIn(FADE_IN).play();
        // The held clip stays playing underneath at a low weight, so her
        // breathing does not stop dead the moment she waves.
        if (this.current) this.current.action.fadeOut(FADE_IN);
        this.gesture = g;
        this._after = then;
        return true;
    }

    /** Sit down properly: the transition, then the pose it leads into. */
    sit() { return this.once('sit_down', 'sit') || this.play('sit'); }
    /** And get back up. */
    stand() { return this.once('stand_up', null) || this._restoreIdle(); }

    setMood(m) {
        if (m === this.mood) return;
        this.mood = m;
        if (!this.gesture) this._restoreIdle();
    }

    /** While she is speaking or thinking, the scheduler keeps out of the way. */
    setBusy(b) { this._busy = !!b; }

    /**
     * The idle that suits her mood, falling back to the plain one. Picking by
     * mood here rather than at every call site is what lets `setMood` alone
     * change how she stands.
     */
    _restoreIdle() {
        const byMood = [...this.clips.values()].find(
            (c) => c.meta.role === 'idle' && c.meta.mood?.includes(this.mood));
        const id = byMood?.meta.id ?? 'idle';
        if (!this.play(id) && this.current) this.current.action.fadeIn(FADE_OUT).play();
        return true;
    }

    /**
     * Weighted pick among the gestures this mood allows. A clip with no mood
     * list suits any of them.
     */
    _pickGesture() {
        const pool = [...this.clips.values()].filter((c) =>
            c.meta.role === 'gesture' &&
            (!c.meta.mood?.length || c.meta.mood.includes(this.mood)));
        if (!pool.length) return null;
        const total = pool.reduce((s, c) => s + (c.meta.weight ?? 1), 0);
        let r = Math.random() * total;
        for (const c of pool) if ((r -= c.meta.weight ?? 1) <= 0) return c.meta.id;
        return pool[pool.length - 1].meta.id;
    }

    /**
     * @param {number} dt seconds since the last frame
     * @param {number} t  milliseconds, monotonic
     */
    update(dt, t) {
        this.mixer.update(dt);
        if (!this.ready || this._busy || this.gesture) return;
        // Long and irregular on purpose. A character who does something cute on
        // a fixed twelve-second timer stops being cute on about the third one.
        if (t > this._nextAt) {
            if (this._nextAt) { const g = this._pickGesture(); if (g) this.once(g); }
            this._nextAt = t + 9000 + Math.random() * 16000;
        }
    }

    dispose() {
        this.mixer.stopAllAction();
        this.mixer.uncacheRoot(this.vrm.scene);
    }
}
