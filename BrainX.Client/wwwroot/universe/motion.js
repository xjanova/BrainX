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

/** Frame-rate independent approach factor. */
const ease = (rate, dt) => 1 - Math.exp(-rate * dt);

/**
 * Crossfade lengths, seconds. A gesture starts from frame 0 of its clip, which
 * can be a long way from the pose she is in, so coming IN too fast is a snap;
 * going out is gentler still, because a one-shot ends clamped on its last frame
 * and has further to travel back.
 */
const FADE_IDLE = 0.5;
const FADE_IN = 0.35;
const FADE_OUT = 0.45;

export class Motion {
    /**
     * @param {object} vrm  the loaded VRM (gltf.userData.vrm)
     * @param {string} base directory holding clips.json and the .fbx files
     */
    constructor(vrm, base = './avatar/') {
        this.vrm = vrm;
        this.base = base.endsWith('/') ? base : base + '/';
        this.mixer = new THREE.AnimationMixer(vrm.scene);
        this.clips = new Map();       // id -> {action, meta, want}
        this.current = null;          // the idle or pose currently held
        this.gesture = null;          // a one-shot playing over it
        this.mood = 'neutral';
        this.missing = [];
        this._nextAt = 0;
        this._busy = false;           // true while she is speaking or working
        this._after = null;
        this._gestT = 0;
        this._fade = FADE_IDLE;
    }

    /** Read the manifest and retarget everything it can find. */
    async load() {
        let manifest;
        try {
            const res = await fetch(this.base + 'clips.json');
            if (!res.ok) throw new Error('HTTP ' + res.status);
            manifest = await res.json();
        } catch (e) {
            console.warn('[motion] no clips.json \u2014', e.message);
            return this;
        }

        // Sequential, not parallel. FBXLoader parses on the main thread, and
        // twenty of them at once stalls the first frames for seconds; one at a
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
            action.enabled = true;
            action.setEffectiveWeight(0);
            this.clips.set(meta.id, { action, meta, want: 0 });
        }

        if (this.missing.length)
            console.info(`[motion] ${this.clips.size} loaded, ${this.missing.length} not present:`,
                         this.missing.join(', '));
        this._restoreIdle();
        return this;
    }

    get ready() { return this.clips.size > 0; }
    get names() { return [...this.clips.keys()]; }

    /**
     * Hold a looping clip: an idle, or a pose like sitting or walking.
     *
     * WEIGHTS ARE DRIVEN IN update(), not by three's fade scheduler.
     * crossFadeFrom leaves its source action with `enabled = false`, and
     * clampWhenFinished parks a one-shot with `paused = true` \u2014 a disabled
     * action ignores fadeIn, and a paused one never advances its fadeOut.
     * Between them the idle never came back and the finished gesture stayed at
     * full weight for the rest of the session: measured, her arm ended 28.8
     * degrees off the idle pose and jumped 11.3 degrees in one frame. Owning
     * the weights outright removes every one of those states.
     */
    play(id, fade = FADE_IDLE) {
        const next = this.clips.get(id);
        if (!next) return false;
        const changed = next !== this.current;
        this.current = next;
        this._fade = fade;
        return changed;
    }

    /**
     * Play a one-shot over whatever is held, then come back to it. `then` names
     * a clip to hold afterwards instead \u2014 that is how a transition works:
     * sit_down runs once and leaves her in sit.
     */
    once(id, then = null) {
        const g = this.clips.get(id);
        if (!g) return false;
        g.action.reset();
        g.action.enabled = true;
        g.action.paused = false;
        g.action.setEffectiveWeight(0);
        g.action.play();
        this.gesture = g;
        this._gestT = 0;
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
        this.play(id, FADE_OUT);
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
        // A gesture owns the body for its own length and then hands back. Its
        // weight is a plain ramp up, hold, ramp down, computed from its own
        // playback time \u2014 so it cannot get stuck part way, whatever else
        // happened while it was running.
        let gw = 0;
        if (this.gesture) {
            this._gestT += dt;
            const dur = this.gesture.action.getClip().duration;
            const rise = Math.min(1, this._gestT / FADE_IN);
            const fall = Math.min(1, Math.max(0, (dur - this._gestT) / FADE_OUT));
            gw = Math.max(0, Math.min(rise, fall));
            if (this._gestT >= dur) {
                const after = this._after;
                this.gesture = null; this._after = null;
                if (after) this.play(after, FADE_OUT); else this._restoreIdle();
                gw = 0;
            }
        }

        // Everything wants zero except the clip being held and the gesture over
        // it, and every weight EASES toward its target \u2014 so no path
        // through this can snap, in whatever order things were asked for.
        for (const c of this.clips.values()) c.want = 0;
        if (this.current) this.current.want = 1 - gw;
        if (this.gesture) this.gesture.want = gw;

        const k = ease(2.2 / Math.max(0.05, this._fade), dt);
        for (const c of this.clips.values()) {
            const w = c.action.getEffectiveWeight();
            const next = w + (c.want - w) * k;
            if (next < 0.002 && c.want === 0) {
                if (c.action.isRunning()) c.action.stop();
                c.action.setEffectiveWeight(0);
            } else {
                c.action.enabled = true;
                c.action.paused = false;
                c.action.setEffectiveWeight(next);
                if (!c.action.isRunning()) c.action.play();
            }
        }

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
