// idle.js — how she stands when nothing else is driving her.
//
// WHY THIS EXISTS AT ALL. VRM 1.0 requires the model to ship in a T-pose: that
// is the rest pose every normalized bone starts from, and with no clip playing
// it is what you see. Arms straight out is not a neutral pose, it is a
// measuring pose, and a character who greets you in it reads as an asset in a
// viewer rather than as someone in the room.
//
// WHY IT IS PROCEDURAL AND NOT A CLIP. Standing still is the one thing a canned
// loop is worst at. A clip repeats exactly, and stillness is where a repeat is
// most visible — the same breath at the same interval forever is how you notice
// the loop. Sine waves at frequencies that do not divide into each other never
// line up, so she never quite repeats. It also costs nothing to ship, works
// before a single .fbx exists, and layers under anything that does arrive.
//
// WHY IT BLENDS INSTEAD OF SETTING. When a real clip IS playing, the mixer has
// already written these bones. Overwriting would throw the clip away; ignoring
// them would drop the breathing. So the pose is a target the bones are slerped
// TOWARD by a weight the caller controls — full when she is idle, a whisper
// when a clip has the body.

import * as THREE from 'three';

/**
 * The relaxed standing pose, as Euler offsets from the T-pose, in radians.
 *
 * Arms are the whole job: from T-pose they have to come down about 70 degrees,
 * roll slightly inward so the elbows read as elbows, and carry a little bend —
 * a perfectly straight arm hanging down looks like a doll's. The rest is small.
 */
const POSE = {
    leftShoulder:  [0, 0, -0.06],
    rightShoulder: [0, 0, 0.06],
    // z drops the arm, y swings it a touch forward of the seam, x rolls it in.
    leftUpperArm:  [0.10, 0.14, -1.24],
    rightUpperArm: [0.10, -0.14, 1.24],
    leftLowerArm:  [0, -0.28, -0.14],
    rightLowerArm: [0, 0.28, 0.14],
    leftHand:      [0, 0, -0.12],
    rightHand:     [0, 0, 0.12],
    spine:         [0.020, 0, 0],
    chest:         [-0.012, 0, 0],
    upperChest:    [-0.008, 0, 0],
    neck:          [0.030, 0, 0],
    head:          [-0.022, 0, 0],
    // Feet slightly apart and turned out, or she stands like a soldier.
    leftUpperLeg:  [0, 0, 0.030],
    rightUpperLeg: [0, 0, -0.030],
    leftFoot:      [0, 0.06, 0],
    rightFoot:     [0, -0.06, 0],
};

/** A hand at rest is not flat. Every finger joint curls a little. */
const FINGER_CURL = 0.26;
const FINGERS = ['Thumb', 'Index', 'Middle', 'Ring', 'Little'];
const SEGMENTS = ['Proximal', 'Intermediate', 'Distal'];

export class Idle {
    constructor(vrm) {
        this.vrm = vrm;
        this.bones = [];

        const add = (name, euler) => {
            const node = vrm.humanoid?.getNormalizedBoneNode(name);
            if (!node) return;               // optional bones (upperChest, toes) may be absent
            this.bones.push({
                name, node,
                base: new THREE.Quaternion().setFromEuler(
                    new THREE.Euler(euler[0], euler[1], euler[2], 'XYZ')),
                target: new THREE.Quaternion(),
            });
        };

        for (const [name, euler] of Object.entries(POSE)) add(name, euler);

        for (const side of ['left', 'right']) {
            const sign = side === 'left' ? -1 : 1;
            for (const f of FINGERS) {
                for (const seg of SEGMENTS) {
                    // The thumb curls around a different axis from the fingers,
                    // which is the difference between a relaxed hand and a claw.
                    const curl = f === 'Thumb' ? FINGER_CURL * 0.55 : FINGER_CURL;
                    add(`${side}${f}${seg}`,
                        f === 'Thumb' ? [0, sign * curl, 0] : [0, 0, sign * curl]);
                }
            }
        }

        this.hips = vrm.humanoid?.getNormalizedBoneNode('hips') ?? null;
        this.hipsRest = this.hips ? this.hips.position.clone() : null;
        this._q = new THREE.Quaternion();
        this._e = new THREE.Euler();
    }

    /**
     * @param {number} t      seconds, monotonic
     * @param {number} weight 1 = she is standing there; lower while a clip drives her
     */
    apply(t, weight = 1) {
        if (weight <= 0.001 || !this.bones.length) return;

        // Three oscillators, deliberately not harmonically related, so the pose
        // never returns to exactly where it was. Breathing is the fastest and
        // the only one big enough to notice on its own.
        const breath = Math.sin(t * 0.95);
        const sway = Math.sin(t * 0.31);
        const drift = Math.sin(t * 0.23), drift2 = Math.sin(t * 0.17);

        for (const b of this.bones) {
            let x = 0, y = 0, z = 0;
            switch (b.name) {
                // The chest opens on the inhale and the shoulders ride with it.
                case 'chest':      x = -breath * 0.026; break;
                case 'upperChest': x = -breath * 0.018; break;
                case 'spine':      x = breath * 0.010 + sway * 0.006; z = -sway * 0.020; break;
                // The head does not sit still on a still body; it drifts.
                case 'neck':       x = breath * 0.008; y = drift * 0.045; z = sway * 0.012; break;
                case 'head':       y = drift2 * 0.035; x = -drift * 0.020; z = -sway * 0.010; break;
                // Arms hang from the shoulders, so they inherit the sway late
                // and slightly damped — that lag is most of what sells it.
                case 'leftShoulder':  x = -breath * 0.020; break;
                case 'rightShoulder': x = -breath * 0.020; break;
                case 'leftUpperArm':  z = -breath * 0.014 - sway * 0.030; break;
                case 'rightUpperArm': z = breath * 0.014 - sway * 0.030; break;
                case 'leftLowerArm':  y = -breath * 0.020; break;
                case 'rightLowerArm': y = breath * 0.020; break;
            }

            this._e.set(x, y, z, 'XYZ');
            b.target.setFromEuler(this._e).premultiply(b.base);
            // Slerp rather than assign: at weight 1 this lands exactly on the
            // pose, and below it the clip underneath keeps its say.
            b.node.quaternion.slerp(b.target, weight);
        }

        // Weight shifts from one foot to the other, and breathing lifts her a
        // little. Both are centimetres — any more and she is bobbing, not
        // standing.
        if (this.hips && this.hipsRest) {
            this.hips.position.set(
                this.hipsRest.x + sway * 0.012 * weight,
                this.hipsRest.y + breath * 0.004 * weight,
                this.hipsRest.z);
        }
    }
}
