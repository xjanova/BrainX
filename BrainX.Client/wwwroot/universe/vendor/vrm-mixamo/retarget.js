// Mixamo FBX -> VRM humanoid animation retargeting.
//
// Adapted from vrm-mixamo-retarget 1.0.3 (MIT, github.com/saori-eth/vrm-mixamo-retargeter),
// which is itself the retargeting routine published in pixiv/three-vrm's examples.
// Converted from TypeScript to plain ES modules because this project has no
// build step, and pulling in rollup to compile 240 lines would cost more than
// it saves.
//
// WHY RETARGETING IS NEEDED AT ALL. Mixamo and VRM both standardise their
// skeletons, but not to each other: Mixamo emits `mixamorigLeftArm` where VRM
// wants `leftUpperArm`, and — more importantly — the two rest poses differ. A
// track cannot simply be renamed, because a rotation is only meaningful
// relative to the pose it was authored against. Each key is therefore rebuilt
// as (parent's rest world rotation) * (the key) * (inverse rest world
// rotation), which re-expresses the same motion against the VRM's rest pose.
//
// AND WHY THE HIPS ARE SCALED. Mixamo animates a human of roughly human
// proportions; a VRoid avatar is 1.4m with a large head and short legs. Hip
// TRANSLATION carried over unscaled makes her bob far too high and skate. The
// scale factor is the ratio of the two hip heights, measured from the models
// themselves rather than assumed.

import * as THREE from 'three';
import { FBXLoader } from '../three/jsm/loaders/FBXLoader.js';

/** Mixamo rig name -> VRM humanoid bone name. Both sides are fixed standards. */
export const MIXAMO_TO_VRM = {
    mixamorigHips: 'hips',
    mixamorigSpine: 'spine',
    mixamorigSpine1: 'chest',
    mixamorigSpine2: 'upperChest',
    mixamorigNeck: 'neck',
    mixamorigHead: 'head',
    mixamorigLeftShoulder: 'leftShoulder',
    mixamorigLeftArm: 'leftUpperArm',
    mixamorigLeftForeArm: 'leftLowerArm',
    mixamorigLeftHand: 'leftHand',
    mixamorigLeftHandThumb1: 'leftThumbMetacarpal',
    mixamorigLeftHandThumb2: 'leftThumbProximal',
    mixamorigLeftHandThumb3: 'leftThumbDistal',
    mixamorigLeftHandIndex1: 'leftIndexProximal',
    mixamorigLeftHandIndex2: 'leftIndexIntermediate',
    mixamorigLeftHandIndex3: 'leftIndexDistal',
    mixamorigLeftHandMiddle1: 'leftMiddleProximal',
    mixamorigLeftHandMiddle2: 'leftMiddleIntermediate',
    mixamorigLeftHandMiddle3: 'leftMiddleDistal',
    mixamorigLeftHandRing1: 'leftRingProximal',
    mixamorigLeftHandRing2: 'leftRingIntermediate',
    mixamorigLeftHandRing3: 'leftRingDistal',
    mixamorigLeftHandPinky1: 'leftLittleProximal',
    mixamorigLeftHandPinky2: 'leftLittleIntermediate',
    mixamorigLeftHandPinky3: 'leftLittleDistal',
    mixamorigRightShoulder: 'rightShoulder',
    mixamorigRightArm: 'rightUpperArm',
    mixamorigRightForeArm: 'rightLowerArm',
    mixamorigRightHand: 'rightHand',
    mixamorigRightHandThumb1: 'rightThumbMetacarpal',
    mixamorigRightHandThumb2: 'rightThumbProximal',
    mixamorigRightHandThumb3: 'rightThumbDistal',
    mixamorigRightHandIndex1: 'rightIndexProximal',
    mixamorigRightHandIndex2: 'rightIndexIntermediate',
    mixamorigRightHandIndex3: 'rightIndexDistal',
    mixamorigRightHandMiddle1: 'rightMiddleProximal',
    mixamorigRightHandMiddle2: 'rightMiddleIntermediate',
    mixamorigRightHandMiddle3: 'rightMiddleDistal',
    mixamorigRightHandRing1: 'rightRingProximal',
    mixamorigRightHandRing2: 'rightRingIntermediate',
    mixamorigRightHandRing3: 'rightRingDistal',
    mixamorigRightHandPinky1: 'rightLittleProximal',
    mixamorigRightHandPinky2: 'rightLittleIntermediate',
    mixamorigRightHandPinky3: 'rightLittleDistal',
    mixamorigLeftUpLeg: 'leftUpperLeg',
    mixamorigLeftLeg: 'leftLowerLeg',
    mixamorigLeftFoot: 'leftFoot',
    mixamorigLeftToeBase: 'leftToes',
    mixamorigRightUpLeg: 'rightUpperLeg',
    mixamorigRightLeg: 'rightLowerLeg',
    mixamorigRightFoot: 'rightFoot',
    mixamorigRightToeBase: 'rightToes',
};

/**
 * Rebuild a Mixamo clip against a VRM's rest pose.
 *
 * @param {THREE.Group} fbx    a loaded Mixamo FBX
 * @param {object} vrm         the target VRM (from gltf.userData.vrm)
 * @param {object} [opts]      {name, clipName, boneMap, quiet}
 * @returns {THREE.AnimationClip|null}
 */
export function retarget(fbx, vrm, opts = {}) {
    const { name = 'clip', clipName = 'mixamo.com', boneMap = {}, quiet = false } = opts;
    const map = { ...MIXAMO_TO_VRM, ...boneMap };
    const warn = (m) => { if (!quiet) console.warn('[retarget]', m); };

    const clip = THREE.AnimationClip.findByName(fbx.animations, clipName)
              ?? fbx.animations[0];
    if (!clip) { warn(`no clip "${clipName}" in the FBX`); return null; }

    const tracks = [];
    const restInverse = new THREE.Quaternion();
    const parentRest = new THREE.Quaternion();
    const q = new THREE.Quaternion();
    const v = new THREE.Vector3();

    const motionHips = fbx.getObjectByName('mixamorigHips')?.position.y;
    const vrmHipsY = vrm.humanoid?.getNormalizedBoneNode('hips')?.getWorldPosition(v).y;
    const vrmRootY = vrm.scene.getWorldPosition(v).y;
    if (!motionHips || vrmHipsY == null) {
        warn('cannot measure hip heights; refusing to guess the scale');
        return null;
    }
    const hipScale = Math.abs(vrmHipsY - vrmRootY) / motionHips;

    // VRM 0.x faces the other way down Z, so its rotations and translations are
    // mirrored relative to 1.0. Ours is 1.0, but the check is cheap and keeps
    // this usable if a 0.x model ever turns up.
    const isVrm0 = vrm.meta?.metaVersion === '0';

    for (const track of clip.tracks) {
        const [rigName, prop] = track.name.split('.');
        const vrmBone = map[rigName];
        if (!vrmBone) continue;
        const node = vrm.humanoid?.getNormalizedBoneNode(vrmBone);
        if (!node) { warn(`VRM has no ${vrmBone} (from ${rigName})`); continue; }

        const rigNode = fbx.getObjectByName(rigName);
        rigNode?.getWorldQuaternion(restInverse).invert();
        rigNode?.parent?.getWorldQuaternion(parentRest);

        if (track instanceof THREE.QuaternionKeyframeTrack) {
            const values = Float32Array.from(track.values);
            for (let i = 0; i < values.length; i += 4) {
                q.fromArray(values, i).premultiply(parentRest).multiply(restInverse);
                q.toArray(values, i);
                if (isVrm0) { values[i] = -values[i]; values[i + 2] = -values[i + 2]; }
            }
            tracks.push(new THREE.QuaternionKeyframeTrack(
                `${node.name}.${prop}`, track.times, values));
        } else if (track instanceof THREE.VectorKeyframeTrack) {
            const values = track.values.map((x, i) =>
                (isVrm0 && i % 3 !== 1 ? -x : x) * hipScale);
            tracks.push(new THREE.VectorKeyframeTrack(
                `${node.name}.${prop}`, track.times, values));
        }
    }

    if (!tracks.length) { warn('nothing matched — is this a Mixamo rig?'); return null; }
    return new THREE.AnimationClip(name, clip.duration, tracks);
}

/** Load a Mixamo .fbx and retarget it in one step. */
export async function loadMixamo(url, vrm, opts = {}) {
    const fbx = await new FBXLoader().loadAsync(url);
    return retarget(fbx, vrm, { name: opts.name ?? url.split('/').pop(), ...opts });
}
