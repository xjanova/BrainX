// sky.js — the room's fourth wall. A Milky Way that turns when the camera
// turns, and keeps turning slowly when nobody is touching it.
//
// WHY A SPHERE AND NOT `scene.background`. three.js takes an equirectangular
// texture as scene.background happily enough, but it converts it to a cube map
// first — six faces at the image's own height, which for a 4K panorama is a
// hundred megabytes of VRAM to back a 400px window. An inverted sphere costs
// the texture and nothing more, and hands back direct control of the tilt and
// the dimming. The sphere is re-centred on the camera every frame, so it has
// no parallax and behaves exactly like a backdrop at infinity: she can be
// panned, dollied and zoomed without sliding across it.
//
// WHY IT TURNS AT ALL. The drag already orbits the camera around her; with a
// flat gradient behind, that orbit had nothing to move against and read as her
// spinning on a turntable rather than as the room going past. Give the camera
// something fixed in the world to sweep over and the same drag suddenly reads
// as movement. That is the whole trick — the sky does not follow the drag, it
// stays PUT, and staying put is what makes it move on screen.
//
// AND WHY IT DRIFTS. Left alone she breathes and fidgets and the camera holds
// still; a dead-still sky behind a living body is the one thing on screen that
// looks switched off. The drift is a real celestial rotation about a tilted
// axis, not a yaw spin, so the band rises and sets over the cycle instead of
// sliding past like a poster on rollers.
//
// WHY IT IS NOT STRETCHED OVER THE WHOLE SKY. The obvious mapping — one
// panorama, 360 degrees — is the wrong one here, and it is worth saying why
// because it looks right until you run it. The lens is 28 degrees vertical in
// a 400px window, so a whole-sky mapping puts about four percent of the image
// on screen: not a galaxy, a patch of grain that could be sensor noise. The
// panorama is wound round SPAN degrees instead and repeated, which is what
// makes the band read AS a band — and which the seam repair below is what
// buys, because a join you would have got away with once now comes round
// three times.
//
// The seam repair and the top/bottom fade live in panorama.js, which the
// dashboard's sky uses too — same image, same two faults to fix, and they are
// properties of equirect projection rather than of this window. Read the why
// there. What is local is the colour they fade into: this canvas is composited
// over the page's own gradient, so the void has to BE that gradient's colour
// or the sky ends on a visible edge. The bottom fades harder and further than
// the top, because below her is where a floor would be, and a dark floor is
// what stops her reading as a cut-out pasted on a poster.

import * as THREE from 'three';
import { loadPanorama } from './panorama.js';

/** The page's own background colour — what the sky fades out into. */
const VOID = [7, 10, 30];

/** Full turn in ~11 minutes. Present, never something you catch moving. */
const DRIFT = 0.0095;               // rad/s

/** Tilt of the axis it turns about, degrees. 0 would slide, not rise and set. */
const TILT = 13;

/**
 * Degrees of longitude one copy of the panorama is wound across. Above this
 * the band goes flat and grainy; below it you start to catch the repeat coming
 * round. 124 puts one copy either side of her and none of the join in frame.
 */
const SPAN = 124;

/**
 * Degrees of latitude it is stretched over, centred on the horizon. Held apart
 * from SPAN rather than falling out of the image's 2:1 shape, because the
 * honest 62 degrees leaves nothing but void the moment the camera is dragged
 * up — and a sky you can tip the camera out of is a ribbon. 1.35x taller than
 * true is invisible on cloud; it would not be on anything with a straight line
 * in it.
 */
const HEIGHT = 84;

/** How much of the panorama's brightness survives. It is scenery, not subject. */
const DIM = 0x8390b4;

/** Far enough behind her to never intersect, inside the camera's far plane. */
const RADIUS = 30;

export class Sky {
    /**
     * @param {THREE.Scene}  scene
     * @param {THREE.Camera} camera  followed in position, never in rotation
     * @param {{url?:string, drift?:number, anisotropy?:number}} opts
     */
    constructor(scene, camera, opts = {}) {
        this.scene = scene;
        this.camera = camera;
        this.drift = opts.drift ?? DRIFT;
        // The band is seen edge-on toward the top and bottom of its strip,
        // which is exactly the case trilinear filtering smears and anisotropic
        // filtering does not. Comes from the renderer; 1 is "nobody told us".
        this._aniso = opts.anisotropy ?? 1;

        this.mesh = null;
        this._dead = false;
        this._angle = 0;
        // Unit axis, tilted out of vertical, about which the whole sky turns.
        this._axis = new THREE.Vector3(
            Math.sin(THREE.MathUtils.degToRad(TILT)), Math.cos(THREE.MathUtils.degToRad(TILT)), 0);
        // 0 = drifting on its own, 1 = the owner has hold of the camera. Eased
        // rather than switched: a drift that stops dead the instant you touch
        // the mouse announces itself, and the point of it is to go unnoticed.
        this._grip = 0;
        this._wantGrip = 0;

        // Resolved against this MODULE, not the page: the same file is loaded
        // by two different windows sitting at two different paths.
        this.loaded = this._load(opts.url ?? new URL('./sky/milkyway.jpg', import.meta.url).href);
    }

    /**
     * The sky is a backdrop, so a missing or broken image is not an error worth
     * stopping for: nothing is added, the canvas stays transparent, and the
     * page's own gradient shows through exactly as it did before there was one.
     */
    async _load(url) {
        const canvas = await loadPanorama(url, { voidColor: VOID, top: 0.15, bottom: 0.24 });
        if (!canvas) return null;
        // A megabyte and a half arrives long after the window could have been
        // closed. Adding to a scene that has already been torn down leaks the
        // texture and every later dispose() misses it, because dispose ran
        // before there was anything to find.
        if (this._dead) return null;

        const tex = new THREE.CanvasTexture(canvas);
        tex.colorSpace = THREE.SRGBColorSpace;
        // Round and round horizontally; clamped vertically, so above and below
        // the band the sampler runs off the image into the rows that were
        // faded to flat void — the sky simply empties out instead of stacking
        // a second galaxy over the first one.
        tex.wrapS = THREE.RepeatWrapping;
        tex.wrapT = THREE.ClampToEdgeWrapping;
        const ky = 180 / HEIGHT;
        tex.repeat.set(360 / SPAN, ky);
        // Centred on the horizon: (1-ky)/2 is the offset that keeps v = 0.5 —
        // the middle of the image — on the equator whatever HEIGHT is set to.
        tex.offset.set(0, (1 - ky) / 2);
        tex.anisotropy = this._aniso;

        this.mesh = new THREE.Mesh(
            // Denser than the usual 64x32 skybox on purpose: UVs interpolate
            // linearly across a triangle while the mapping is angular, and at
            // this field of view only a couple of segments span the screen —
            // few enough for that difference to show as a warp.
            new THREE.SphereGeometry(RADIUS, 128, 64),
            new THREE.MeshBasicMaterial({
                map: tex,
                color: new THREE.Color(DIM),
                side: THREE.BackSide,
                // Writes no depth and goes first, so it can never occlude her
                // however the sorter feels about a 30m sphere on a given frame.
                depthWrite: false,
                toneMapped: false,
                fog: false,
            }));
        this.mesh.renderOrder = -1;
        // It is centred on the camera every frame; there is nothing to cull it
        // against and nothing to gain from asking.
        this.mesh.frustumCulled = false;
        this.scene.add(this.mesh);
        return this.mesh;
    }

    /**
     * Tell the sky the owner has taken (or let go of) the camera.
     * @param {boolean} held
     */
    grip(held) { this._wantGrip = held ? 1 : 0; return this; }

    /** @param {number} dt seconds */
    update(dt) {
        const m = this.mesh;
        if (!m) return this;

        this._grip += (this._wantGrip - this._grip) * (1 - Math.exp(-2.4 * dt));
        this._angle += this.drift * (1 - this._grip) * dt;

        // Position only. Leaving the rotation alone is what makes a drag turn
        // it: the camera swings, the sky does not, and the sky sweeps past.
        m.position.copy(this.camera.position);
        m.quaternion.setFromAxisAngle(this._axis, this._angle);
        return this;
    }

    dispose() {
        this._dead = true;
        if (!this.mesh) return;
        this.scene.remove(this.mesh);
        this.mesh.geometry.dispose();
        this.mesh.material.map?.dispose();
        this.mesh.material.dispose();
        this.mesh = null;
    }
}
