// framing.js — the camera moves with what she is doing.
//
// WHY THE CAMERA MOVES AT ALL. A fixed wide shot wastes the thing that took the
// most work: when she talks, the expression, the visemes and the eyes are all
// happening in a face that is forty pixels tall. A fixed close shot throws away
// the other thing — the body, the walk, the fidgeting. Neither framing is right
// for both states, so the camera picks.
//
// WHY IT IS MEASURED, NOT TYPED IN. Every target here is derived from her own
// bones at run time: the head node's world height, the model's bounding box.
// Hard-coded distances were wrong the moment the model changed, and they hide
// the reason a number is what it is. `fit` says "get this many metres of
// subject into the frame" and the distance falls out of the field of view.
//
// WHY CRITICAL DAMPING AND NOT A LERP. An exponential lerp toward a target
// never quite arrives and has no notion of speed, so a shot change either
// snaps or drifts. A critically damped spring reaches the target in a
// predictable time and — the part that matters on a face — arrives without
// overshooting, so she never rocks back at the end of a push-in.

import * as THREE from 'three';

/** How much subject to fit vertically, and where to aim, per shot. */
const SHOTS = {
    // Whole of her, with headroom, standing on the bottom edge.
    full: { fit: 1.85, aimY: 0.52, offY: 0.00, offZ: 0 },
    // Chest up. The default while she speaks.
    bust: { fit: 0.62, aimY: 'head', offY: -0.10, offZ: 0 },
    // Closer still, for a mood worth seeing.
    face: { fit: 0.34, aimY: 'head', offY: -0.02, offZ: 0 },
};

export class Framing {
    /**
     * @param {THREE.PerspectiveCamera} camera
     * @param {object} vrm    the loaded VRM, for measuring her
     */
    constructor(camera, vrm) {
        this.camera = camera;
        this.vrm = vrm;
        this.shot = 'full';

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

    /** Where the camera and its aim point WANT to be for the current shot. */
    target(name) {
        const s = SHOTS[name] ?? SHOTS.full;
        const aimY = (s.aimY === 'head' ? this.headY : this.height * s.aimY) + s.offY;
        // Distance is whatever puts `fit` metres across the vertical field of
        // view. Derived, so changing the fov or the model does not silently
        // reframe her.
        const half = THREE.MathUtils.degToRad(this.camera.fov) / 2;
        const dist = (s.fit / 2) / Math.tan(half);
        this._a.set(0, aimY, 0);
        this._t.set(0, aimY, dist + s.offZ);
        return this;
    }

    /** @param {'full'|'bust'|'face'} name */
    set(name) {
        if (name === this.shot || !SHOTS[name]) return this;
        this.shot = name;
        return this;
    }

    /**
     * @param {number} dt      seconds
     * @param {number} settle  seconds to arrive; bigger is lazier
     */
    update(dt, settle = 0.75) {
        this.target(this.shot);
        // Critically damped: no overshoot, arrives in about `settle`.
        const omega = 2 / Math.max(0.05, settle);
        const k = Math.min(1, dt * omega);
        spring(this.pos, this._t, this.vPos, omega, dt);
        spring(this.aim, this._a, this.vAim, omega, dt);
        void k;
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
