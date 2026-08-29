// framing.js — the camera. Picks a shot on its own, and gets out of the way
// when the owner takes hold of it.
//
// WHY THE CAMERA MOVES BY ITSELF. A fixed wide shot wastes the thing that took
// the most work: while she talks, the expression, the visemes and the eyes are
// all happening in a face forty pixels tall. A fixed close shot throws away the
// other thing — the body, the walk, the fidgeting. Neither framing is right for
// both states, so it picks.
//
// AND WHY THE OWNER STILL WINS. Automatic framing that cannot be overridden is
// a camera that argues with you. Drag orbits, right-drag (or shift-drag) pans,
// the wheel zooms, and all three are OFFSETS layered on whatever shot the state
// machine chose — so she can be put anywhere in frame and the push-in when she
// starts talking still happens, from wherever you left her.
//
// WHY EVERYTHING IS MEASURED. Every base target is derived from her own bones
// at run time: the head node's world height, the model's bounding box. Hard
// numbers were wrong the moment the model changed and hid the reason they were
// what they were. `fit` says "get this many metres of subject in frame" and the
// distance falls out of the field of view.
//
// WHY CRITICAL DAMPING. An exponential lerp toward a target never quite arrives
// and has no notion of speed, so a shot change either snaps or drifts. A
// critically damped spring arrives in a predictable time and — the part that
// matters on a face — without overshooting, so she never rocks back at the end
// of a push-in.

import * as THREE from 'three';

/** How much subject to fit vertically, and where to aim, per shot. */
const SHOTS = {
    full: { fit: 1.85, aimY: 0.52, offY: 0.00 },   // all of her, with headroom
    bust: { fit: 0.62, aimY: 'head', offY: -0.10 },  // chest up; while speaking
    face: { fit: 0.34, aimY: 'head', offY: -0.02 },  // for a mood worth seeing
};

const PITCH_LIMIT = 1.05;   // ~60 degrees; past it she is a floor plan

export class Framing {
    /**
     * @param {THREE.PerspectiveCamera} camera
     * @param {object} vrm    the loaded VRM, for measuring her
     */
    constructor(camera, vrm) {
        this.camera = camera;
        this.vrm = vrm;
        this.shot = 'full';

        /** Fraction of the visible width to push her off-centre. */
        this.lateral = 0;
        /** Wheel zoom. 1 is the shot as designed; smaller is closer. */
        this.zoom = 1;
        /** Owner's orbit, radians. */
        this.yaw = 0;
        this.pitch = 0;
        /** Owner's pan, metres, in the camera's own plane. */
        this.panX = 0;
        this.panY = 0;

        const box = new THREE.Box3().setFromObject(vrm.scene);
        this.height = box.getSize(new THREE.Vector3()).y;
        this.headY = vrm.humanoid?.getNormalizedBoneNode('head')
            ?.getWorldPosition(new THREE.Vector3()).y ?? this.height * 0.85;

        this.pos = new THREE.Vector3();
        this.aim = new THREE.Vector3();
        this.vPos = new THREE.Vector3();   // spring velocities
        this.vAim = new THREE.Vector3();
        this._t = new THREE.Vector3();
        this._a = new THREE.Vector3();

        this.target(this.shot);
        this.pos.copy(this._t);
        this.aim.copy(this._a);
        this._commit();
    }

    /** Where the camera and its aim point WANT to be, right now. */
    target(name) {
        const s = SHOTS[name] ?? SHOTS.full;
        const aimY = (s.aimY === 'head' ? this.headY : this.height * s.aimY) + s.offY;
        // Distance is whatever puts `fit` metres across the vertical field of
        // view. Derived, so changing the fov or the model does not silently
        // reframe her.
        const half = THREE.MathUtils.degToRad(this.camera.fov) / 2;
        const dist = (s.fit * this.zoom / 2) / Math.tan(half);

        // Sideways is a DOLLY, not a turn: rotating to look past her skews the
        // perspective across her face, which on a close shot reads immediately
        // as a lens artefact. Moving the camera and its aim together is a pure
        // slide. The owner's pan rides in the same place.
        const visW = 2 * dist * Math.tan(half) * this.camera.aspect;
        const cy = Math.cos(this.yaw), sy = Math.sin(this.yaw);
        const ax = visW * this.lateral + this.panX * cy;
        const az = -this.panX * sy;
        this._a.set(ax, aimY + this.panY, az);

        // Orbit around the aim point.
        const cp = Math.cos(this.pitch);
        this._t.set(
            ax + dist * sy * cp,
            this._a.y + dist * Math.sin(this.pitch),
            az + dist * cy * cp);
        return this;
    }

    /** @param {'full'|'bust'|'face'} name */
    set(name) {
        if (name === this.shot || !SHOTS[name]) return this;
        this.shot = name;
        return this;
    }

    /** Drag to orbit. Pixels in, radians out. */
    orbit(dx, dy) {
        this.yaw -= dx * 0.006;
        this.pitch = Math.max(-PITCH_LIMIT, Math.min(PITCH_LIMIT, this.pitch + dy * 0.005));
        return this;
    }

    /**
     * Drag to slide her around the frame. Scaled by the visible height so a
     * given drag moves her the same distance ON SCREEN whatever the zoom —
     * pixels of pan that mean different amounts at different distances feel
     * broken even when the maths is right.
     */
    pan(dx, dy, viewportH) {
        const half = THREE.MathUtils.degToRad(this.camera.fov) / 2;
        const dist = this.pos.distanceTo(this.aim) || 1;
        const perPixel = (2 * dist * Math.tan(half)) / Math.max(1, viewportH);
        this.panX -= dx * perPixel;
        this.panY += dy * perPixel;
        return this;
    }

    /** Wheel. Clamped: past either end she is a speck or a pair of nostrils. */
    dolly(delta) {
        const z = this.zoom * (delta > 0 ? 1.12 : 1 / 1.12);
        this.zoom = Math.min(1.9, Math.max(0.42, z));
        return this;
    }

    /** Put the owner's offsets back. The automatic shot is untouched. */
    recenter() {
        this.yaw = this.pitch = this.panX = this.panY = 0;
        this.zoom = 1;
        return this;
    }

    /**
     * @param {number} dt      seconds
     * @param {number} settle  seconds to arrive; bigger is lazier
     */
    update(dt, settle = 0.75) {
        this.target(this.shot);
        const omega = 2 / Math.max(0.05, settle);
        spring(this.pos, this._t, this.vPos, omega, dt);
        spring(this.aim, this._a, this.vAim, omega, dt);
        this._commit();
    }

    _commit() {
        this.camera.position.copy(this.pos);
        this.camera.lookAt(this.aim);
    }
}

/**
 * One step of a critically damped spring toward `to`.
 * Stable for large dt, unlike the naive velocity += (to-x)*k form, which
 * explodes the moment a frame runs long — and a frame WILL run long, because
 * the first ones after a clip loads are the slowest in the session.
 */
function spring(x, to, v, omega, dt) {
    const f = 1 + 2 * dt * omega;
    const oo = omega * omega, dtoo = dt * oo;
    const det = f + dt * dtoo;
    for (const c of ['x', 'y', 'z']) {
        const detX = f * x[c] + dt * v[c] + dt * dtoo * to[c];
        const detV = v[c] + dtoo * (to[c] - x[c]);
        x[c] = detX / det;
        v[c] = detV / det;
    }
}
