// idle.js — the procedural body layer: how she holds herself when nothing else
// is driving her, and the small things she does with her head while speaking.
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
// the loop. Oscillators at frequencies that do not divide into each other never
// line up, so she never quite repeats, and the fidget on top is irregular by
// construction. It also costs nothing to ship and worked before a single .fbx
// existed.
//
// WHY THE SPEECH ACCENTS LIVE HERE TOO. A nod on a stressed syllable and a
// breath both end up as a rotation on the neck, and two layers writing the same
// bone from different files is how you get one silently cancelling the other.
// One owner for procedural bone offsets, one blend, one place to look.
//
// WHY IT BLENDS INSTEAD OF SETTING. When a real clip IS playing, the mixer has
// already written these bones. Overwriting would throw the clip away; ignoring
// them would drop the breathing. So the pose is a target the bones are slerped
// TOWARD by a weight the caller controls — full when she is idle, a whisper
// when a clip has the body.

import * as THREE from 'three';

/**
 * Frame-rate independent easing factor.
 *
 * `x += (target - x) * 0.02` looks smooth at a steady 60fps and JUDDERS the
 * moment the frame rate moves, because the constant is per FRAME rather than
 * per second: a long frame eases exactly as far as a short one, so the motion
 * arrives in visible steps. Every ease in this file is a rate in units per
 * second run through this instead. That judder is what "she twitches" was.
 */
const ease = (rate, dt) => 1 - Math.exp(-rate * dt);

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
    //
    // WHY THE ARMS ARE THIS WIDE AND THIS FAR FORWARD. Her skirt flares, and at
    // the Mixamo idle's arm position her gloves sat 30mm INSIDE the cloth —
    // the scalloped hem cut straight across them.
    //
    // The measurement that matters is the FINGERTIP against the skirt at the
    // fingertip's own height, not the hand against the hip. The hand is above
    // the widest part of the flare and looks clear; the fingers hang down into
    // it. Comparing hand-to-hip said 9-12cm of room and was answering a
    // different question entirely.
    //
    // Swinging the arms forward alone does nothing (-2mm). Out alone stalls,
    // because the hand's own COLLIDER pushes the skirt outward as it
    // approaches, so the two track each other. Forward AND out clears it:
    // measured +35mm at the fingertip.
    leftUpperArm:  [0.10, 0.26, -0.96],
    rightUpperArm: [0.10, -0.26, 0.96],
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
    // Barely bent. Not zero, because a knee locked dead straight reads as a
    // mannequin, but nowhere near the 18 degrees the clip asks for.
    leftLowerLeg:  [0.030, 0, 0],
    rightLowerLeg: [0.030, 0, 0],
    leftFoot:      [0, 0.06, 0],
    rightFoot:     [0, -0.06, 0],
};

/**
 * The lower body, which gets its own blend weight.
 *
 * Mixamo's standing idles are BRACED — knees bent about 18 degrees, hips
 * dropped, weight forward. That is correct for the game characters they were
 * authored for and wrong for a girl standing in a room: measured on Breathing
 * Idle, her knee sat at 18.3 degrees and her hips 15mm low, which reads as a
 * permanent half-crouch. The clip's sway is worth keeping; its stance is not,
 * so the legs are pulled back toward standing while the rest of the clip plays
 * at full strength.
 */
const LEGS = new Set([
    'leftUpperLeg', 'rightUpperLeg', 'leftLowerLeg', 'rightLowerLeg',
    'leftFoot', 'rightFoot', 'leftToes', 'rightToes',
]);

/**
 * The arms, which get a weight of their own for the same reason the legs do.
 *
 * A Mixamo idle hangs the arms close to the body, which is right for a
 * character in trousers and wrong for one in a flared skirt: her hands ended up
 * inside the cloth. The correction has to reach the arms even while a clip is
 * playing, so it cannot ride on the general procedural weight of 0.18 — but it
 * must NOT apply during a gesture, or waving and clapping would be flattened
 * into a stand. The caller decides, and only ever turns it on for an idle.
 */
const ARMS = new Set([
    'leftShoulder', 'rightShoulder',
    'leftUpperArm', 'rightUpperArm', 'leftLowerArm', 'rightLowerArm',
    'leftHand', 'rightHand',
]);

/** A hand at rest is not flat. Every finger joint curls a little. */
const FINGER_CURL = 0.26;
const FINGERS = ['Thumb', 'Index', 'Middle', 'Ring', 'Little'];
const SEGMENTS = ['Proximal', 'Intermediate', 'Distal'];

export class Idle {
    constructor(vrm) {
        this.vrm = vrm;
        this.bones = [];
        /** Read by the caller and pushed to the `blink` expression. */
        this.blink = 0;

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

        // Fidget. The sine layer alone is honest breathing but it is also
        // perfectly regular, and a body that only ever does the same smooth
        // thing reads as a machine idling. Real stillness is punctuated: a
        // shift of weight, a glance away, a small settle — irregular, and never
        // quite the same size twice.
        this._fidgetAt = 0;
        this._fid = { x: 0, y: 0, z: 0, tx: 0, ty: 0, tz: 0 };

        // Speech accents.
        this._env = 0;          // smoothed loudness
        this._envSlow = 0;      // slower average, to detect a RISE against
        this._nod = 0;          // impulse; decays into a damped oscillation
        this._nodT = 0;
        this._nodLock = 0;      // no second nod on the same syllable
        this._quietFor = 0;     // seconds of silence, for phrase boundaries
        this._wasLoud = false;
        this._blinkAt = 0;
        this._blinkStart = -1e9;

        this.hips = vrm.humanoid?.getNormalizedBoneNode('hips') ?? null;
        this.hipsRest = this.hips ? this.hips.position.clone() : null;
        this._e = new THREE.Euler();
    }

    /**
     * @param {number} t      seconds, monotonic
     * @param {number} weight 1 = she is standing there; lower while a clip drives her
     * @param {number} speech current loudness 0..1, or 0 when she is not talking
     * @param {number} dt     seconds since the last call
     * @param {number} legWeight how hard to pull the legs back toward standing.
     *   The caller decides, because it is only right while she is on her feet —
     *   applied to a sitting clip it would stand her up through the chair.
     * @param {number} armWeight how hard to hold the arms clear of her skirt.
     *   Zero during a gesture, or a wave becomes a stand.
     */
    apply(t, weight = 1, speech = 0, dt = 1 / 60, legWeight = 0, armWeight = 0) {
        this._accents(t, speech, dt);
        if (!this.bones.length) return;
        const legW = Math.max(weight, legWeight);
        const armW = Math.max(weight, armWeight);
        if (weight <= 0.001 && legW <= 0.001 && armW <= 0.001) return;

        // Oscillators, deliberately not harmonically related, so the pose never
        // returns to exactly where it was. Breathing is the fastest and the only
        // one big enough to notice on its own.
        const breath = Math.sin(t * 0.95);
        const sway = Math.sin(t * 0.31);
        const drift = Math.sin(t * 0.23), drift2 = Math.sin(t * 0.17);

        // A new fidget every few seconds, eased into rather than snapped to.
        // The interval is deliberately ragged: anything regular enough to
        // anticipate stops registering as alive after the third repetition.
        if (t > this._fidgetAt) {
            this._fidgetAt = t + 2.2 + Math.random() * 5.5;
            const f = this._fid;
            f.tx = (Math.random() - 0.5) * 0.055;   // head pitch
            f.ty = (Math.random() - 0.5) * 0.130;   // head turn
            f.tz = (Math.random() - 0.5) * 0.045;   // weight lean
        }
        const F = this._fid;
        const k = ease(1.2, dt);
        F.x += (F.tx - F.x) * k;
        F.y += (F.ty - F.y) * k;
        F.z += (F.tz - F.z) * k;

        // A nod is a quick dip and a slower recovery, not a sine — the head
        // drops on the stress and comes back up, and a symmetric wobble reads
        // as a bobblehead.
        const nod = this._nod * Math.sin(this._nodT * 11.0) * Math.exp(-this._nodT * 4.5);

        for (const b of this.bones) {
            let x = 0, y = 0, z = 0;
            switch (b.name) {
                // The chest opens on the inhale and the shoulders ride with it.
                case 'chest':      x = -breath * 0.026; break;
                case 'upperChest': x = -breath * 0.018; break;
                case 'spine':      x = breath * 0.010 + sway * 0.006; z = -sway * 0.020 - F.z * 0.5; break;
                // The head does not sit still on a still body; it drifts. While
                // she talks the nod is added on top of that, split between neck
                // and head so it hinges in two places like a real one.
                case 'neck':
                    x = breath * 0.008 + nod * 0.45;
                    y = drift * 0.045 + F.y * 0.35;
                    z = sway * 0.012; break;
                case 'head':
                    x = -drift * 0.020 + F.x + nod * 0.55;
                    y = drift2 * 0.035 + F.y * 0.65;
                    z = -sway * 0.010 + F.z * 0.4; break;
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
            b.node.quaternion.slerp(b.target,
                LEGS.has(b.name) ? legW : ARMS.has(b.name) ? armW : weight);
        }

        // Weight shifts from one foot to the other, and breathing lifts her a
        // little. Both are centimetres — any more and she is bobbing, not
        // standing.
        // The hips move with the LEGS, not with the general weight: straighten
        // the knees while leaving the hips where a braced clip put them and her
        // feet go through the floor.
        if (this.hips && this.hipsRest) {
            const p = this.hips.position;
            p.x += (this.hipsRest.x + (sway * 0.012 + F.z * 0.10) - p.x) * legW;
            p.y += (this.hipsRest.y + breath * 0.004 - p.y) * legW;
            p.z += (this.hipsRest.z - p.z) * legW;
        }
    }

    /**
     * Everything that keys off the voice: nods on emphasis, a glance at the end
     * of a phrase, and blinking.
     *
     * All of it is driven by the AUDIO rather than by a timer. A nod on a fixed
     * interval lands in the middle of words and reads as a tic; a nod on a rise
     * in loudness lands on the stress, which is where a person puts one.
     */
    _accents(t, speech, dt) {
        this._env += (speech - this._env) * ease(speech > this._env ? 48 : 6.5, dt);
        this._envSlow += (this._env - this._envSlow) * ease(2.1, dt);

        // A RISE against the slow average is emphasis. Comparing against a
        // fixed threshold instead would fire constantly on a loud passage and
        // never on a quiet one.
        this._nodLock -= dt;
        if (this._env > 0.22 && this._env - this._envSlow > 0.13 && this._nodLock <= 0) {
            this._nod = 0.035 + Math.min(0.030, (this._env - this._envSlow) * 0.10);
            this._nodT = 0;
            this._nodLock = 0.55 + Math.random() * 0.7;
        }
        this._nodT += dt;                         // the envelope does the decay

        // Phrase boundary: she was talking and has gone quiet for a moment.
        // People look away and blink at exactly these seams.
        const loud = this._env > 0.10;
        if (loud) { this._quietFor = 0; this._wasLoud = true; }
        else this._quietFor += dt;
        if (this._wasLoud && this._quietFor > 0.28) {
            this._wasLoud = false;
            this._fidgetAt = 0;                   // a fresh glance, now
            this._blinkAt = Math.min(this._blinkAt, t + 0.15);
        }

        // Blinking. Faster while she is speaking, because people do.
        if (t > this._blinkAt) {
            const talking = this._env > 0.05;
            this._blinkAt = t + (talking ? 1.2 + Math.random() * 1.8
                                         : 2.2 + Math.random() * 3.2);
            this._blinkStart = t;
        }
        const bp = (t - this._blinkStart) / 0.13;
        this.blink = bp >= 0 && bp <= 1 ? Math.abs(Math.sin(bp * Math.PI)) : 0;
    }
}
