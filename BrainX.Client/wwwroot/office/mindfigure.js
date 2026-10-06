// mindfigure.js — Mind's real 3D body, rendered tiny so she can stand in the
// pixel-art room.
//
// WHY RENDER HER SMALL INSTEAD OF DRAWING A SPRITE. She already exists as a
// rigged VRM with breathing, clips and a face; a hand-drawn sprite would be a
// second Mind that drifts from the first. So the real model is rendered at the
// room's own resolution — about 40x84 — with antialiasing off, and the room
// scales that up with smoothing off like every other sprite. The low
// resolution IS the pixel look; nothing is faked on top of it except the 1px
// outline the room's sprites all carry.
//
// WHY ONE READBACK PER FRAME. The outline needs the silhouette, and the
// silhouette is only known after the GPU has drawn her. At 40x84 the readback
// is 13KB, and post-processing it in JS (alpha threshold + 4-neighbour
// dilation) is a few thousand array reads — cheaper than a second pass of
// shaders, and it leaves `canvas` a plain 2D canvas the room can drawImage()
// without caring that WebGL was involved.
//
// WHY THE CAMERA IS MEASURED, NOT TYPED. Where her feet land in the frame
// depends on the model's proportions, the camera angle and the pose. Hard
// numbers were wrong the moment any of those changed. So the frame is
// calibrated once at load: render her large, find the silhouette's real
// bounds, then fit the orthographic frustum so her soles sit on the bottom
// row and the floor point between her feet is a known pixel (`anchor`).
//
// ORDER OF WRITES PER FRAME — the same arbitration avatar.js uses, for the same
// reason (get it wrong and one layer silently erases another):
//   1. mixer       clips write the bones they own
//   2. idle        procedural breathing / fidget slerped over the top
//   3. fallbacks   procedural wave / think when the clip for it never arrived
//   4. face        mouth, smile, blink
//   5. vrm.update  normalized -> real rig, look-at, spring bones LAST

import * as THREE from 'three';
import { GLTFLoader } from '../universe/vendor/three/jsm/loaders/GLTFLoader.js';
import { VRMLoaderPlugin, VRMUtils } from '@pixiv/three-vrm';
import { Idle } from '../universe/idle.js';

const STATES = ['idle', 'talking', 'thinking', 'wave'];

/** How long each short-lived state holds before she settles back, seconds. */
const HOLD = { talking: 4.0, thinking: 6.0, wave: 2.4 };

/**
 * The clips she uses here, by manifest id, with the file to fall back to if the
 * manifest is missing or renames them. Loaded one at a time, idle first:
 * FBXLoader parses on the main thread, and the room should not stall for all of
 * them at once.
 *
 * All of them LOOP, and the state's hold time decides how long one plays.
 * Mixamo's Waving is a single 0.53s back-and-forth: played once it is a twitch
 * of the wrist, looped for a couple of seconds it is a wave.
 */
const CLIPS = [
    { id: 'idle',       file: 'Breathing Idle.fbx' },
    { id: 'wave',       file: 'Waving.fbx' },
    { id: 'talk',       file: 'Talking.fbx' },
    { id: 'idle_think', file: 'Thinking.fbx' },
];
const STATE_CLIP = { talking: 'talk', thinking: 'idle_think', wave: 'wave' };
/** How much of each clip she plays. Mixamo's Talking is a lecture — at full
 *  weight her hands swept past both edges of a 40px frame. Blended with the
 *  idle it is someone chatting. */
const CLIP_WEIGHT = { talk: 0.55 };

/** Camera looks down this much, radians. Lower than the room's own angle on
 *  purpose, like the room's sprites: at 20 degrees her fringe hid her eyes
 *  completely and the face read as a blank patch of skin. */
const ELEVATION = THREE.MathUtils.degToRad(10);
/** How far she turns off the camera axis for 'sw' / 'se'. */
const TURN = THREE.MathUtils.degToRad(36);

/** Face marks, the room's own ink (office.js drawFace) plus a violet iris. */
const EYE_INK = [42, 36, 56];
const EYE_IRIS = [72, 70, 98];
const MOUTH_INK = [110, 46, 62];
const MARK_EYE = 1, MARK_SHUT = 2, MARK_MOUTH = 3;

const ease = (rate, dt) => 1 - Math.exp(-rate * dt);
const clamp01 = (x) => (x < 0 ? 0 : x > 1 ? 1 : x);

/**
 * Force trilinear mipmapping on every texture a material uses. VRoid ships
 * some textures without mip filtering; minified twentyfold, those sample one
 * random texel per pixel and her dress turned into a field of orange glitter
 * that crawled every frame. Averaged, each pixel gets the colour of the patch
 * it covers — which is what a pixel artist would have painted there.
 */
function crispTextures(mat) {
    if (!mat) return;
    // MToon keeps its textures in `uniforms` behind prototype getters, so
    // own-property enumeration of the material finds none of them.
    const found = [
        ...Object.values(mat.uniforms ?? {}).map((u) => u?.value),
        ...Object.keys(mat).map((k) => mat[k]),
    ];
    for (const tex of found) {
        if (!tex || !tex.isTexture) continue;
        if (tex.minFilter === THREE.LinearMipmapLinearFilter && tex.generateMipmaps) continue;
        tex.generateMipmaps = true;
        tex.minFilter = THREE.LinearMipmapLinearFilter;
        tex.anisotropy = 1;
        tex.needsUpdate = true;
    }
}

/**
 * The vertices that stand for her eyes and mouth: for each, the one nearest
 * the centre of its mesh (the iris mesh split by side). Followed through the
 * skinning every frame, so the face marks stay on the face through nods,
 * turns and the enlarged head.
 *
 * WHY NOT THE EYE BONES. VRoid's eye bones sit at the eyeball pivots, deep in
 * the skull and 3cm apart; the irises a viewer actually sees are 6.6cm apart.
 * Marks placed on the bones landed between her eyes.
 */
function findFeatures(vrm) {
    let iris = null, mouth = null;
    vrm.scene.traverse((o) => {
        if (!o.isSkinnedMesh || typeof o.applyBoneTransform !== 'function') return;
        const name = [].concat(o.material)[0]?.name ?? '';
        if (!iris && /EyeIris/i.test(name)) iris = o;
        if (!mouth && /FaceMouth/i.test(name)) mouth = o;
    });
    const nearestToCentre = (mesh, keep) => {
        const pos = mesh.geometry.attributes.position;
        const c = new THREE.Vector3(), v = new THREE.Vector3();
        let n = 0;
        for (let i = 0; i < pos.count; i++) {
            v.fromBufferAttribute(pos, i);
            if (keep(v)) { c.add(v); n++; }
        }
        if (!n) return null;
        c.divideScalar(n);
        let best = -1, bd = Infinity;
        for (let i = 0; i < pos.count; i++) {
            v.fromBufferAttribute(pos, i);
            if (!keep(v)) continue;
            const d = v.distanceToSquared(c);
            if (d < bd) { bd = d; best = i; }
        }
        return best < 0 ? null : { mesh, idx: best };
    };
    const eyes = iris
        ? [nearestToCentre(iris, (v) => v.x > 0), nearestToCentre(iris, (v) => v.x < 0)].filter(Boolean)
        : [];
    return { eyes, mouth: mouth ? nearestToCentre(mouth, () => true) : null };
}

function hexRgb(hex) {
    const m = /^#?([0-9a-f]{6})$/i.exec(String(hex || ''));
    const n = m ? parseInt(m[1], 16) : 0x1a1220;
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

export class MindFigure {
    /**
     * @param {object}  o
     * @param {string}  [o.base]    folder URL holding minde.vrm and the clips
     * @param {string}  [o.model]   file name of the VRM inside `base`
     * @param {number}  [o.width]   internal render width, px (40)
     * @param {number}  [o.height]  internal render height, px (84)
     * @param {number}  [o.fps]     render rate cap (24)
     * @param {string|false} [o.outline] outline ink, or false for none
     * @param {number}  [o.headScale] head enlargement; >1 is the sprite-style
     *   big head that keeps a face legible at this size (1.2)
     */
    constructor({ base = '../universe/avatar/', model = 'minde.vrm',
                  width = 40, height = 84, fps = 24, outline = '#1a1220',
                  headScale = 1.2 } = {}) {
        this._headScale = headScale > 0 ? headScale : 1;
        this.width = Math.max(8, Math.round(width));
        this.height = Math.max(16, Math.round(height));
        this.base = String(base).endsWith('/') ? String(base) : base + '/';
        this.model = model;

        /** What the room draws. Plain 2D canvas, transparent, width x height. */
        this.canvas = document.createElement('canvas');
        this.canvas.width = this.width;
        this.canvas.height = this.height;
        this._ctx = this.canvas.getContext('2d');
        this._img = this._ctx.createImageData(this.width, this.height);
        this._buf = new Uint8Array(this.width * this.height * 4);
        this._mask = new Uint8Array(this.width * this.height);
        // Face marks for the frame in flight and the one being composed.
        this._marksA = new Float32Array(10);
        this._marksB = new Float32Array(10);
        this._markFlip = false;
        this._async = false;
        this._pending = null;     // { sync, pbo, marks } of the frame in flight

        /** Where her feet meet the floor, in canvas pixels from the top-left.
         *  Final after `ready`. */
        this.anchor = { x: this.width / 2, y: this.height - 1 };
        /** Increments every time `canvas` changes. */
        this.frame = 0;
        this.state = 'idle';
        this.facing = 'sw';

        this._ink = outline === false ? null : hexRgb(outline);
        this._frameMs = 1000 / Math.max(1, fps);
        this._lastMs = null;
        this._t = 0;
        this._stateLeft = 0;
        this._yaw = -TURN;
        this._clips = new Map();       // id -> { action, w, want }
        this._fb = { wave: 0, think: 0 };  // procedural-fallback weights
        this._smile = 0;
        this._mouth = 0;
        this._live = false;
        this._disposed = false;
        this._errors = 0;

        this.ready = this._load();
        // Marks the rejection handled for callers that never attach a catch;
        // anyone who does still sees it.
        this.ready.catch(() => {});
    }

    // ── loading ─────────────────────────────────────────────────────────

    async _load() {
        try {
            await this._loadInner();
        } catch (e) {
            // A failed load still holds a WebGL context, and browsers allow
            // only a handful per page. The caller just hides her; it should
            // not also have to remember to clean up a figure that never was.
            this.dispose();
            throw e;
        }
    }

    async _loadInner() {
        const W = this.width, H = this.height;

        const r = new THREE.WebGLRenderer({
            antialias: false, alpha: true, premultipliedAlpha: true,
            preserveDrawingBuffer: false, stencil: false,
            powerPreference: 'low-power',
        });
        this.renderer = r;
        r.setPixelRatio(1);
        r.setSize(W, H, false);
        r.setClearColor(0x000000, 0);
        // A lost context (driver reset, GPU process crash) freezes her on her
        // last frame; three rebuilds its state on restore and so does she.
        // The frame in flight died with the old context.
        this._onLost = () => { this._live = false; this._pending = null; };
        this._onRestored = () => { if (this.vrm && !this._disposed) this._live = true; };
        r.domElement.addEventListener('webglcontextlost', this._onLost);
        r.domElement.addEventListener('webglcontextrestored', this._onRestored);

        this.scene = new THREE.Scene();
        this.camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0.1, 30);
        const aimY = 0.75;
        this.camera.position.set(0, aimY + Math.sin(ELEVATION) * 10, Math.cos(ELEVATION) * 10);
        this.camera.lookAt(0, aimY, 0);
        this.camera.updateMatrixWorld(true);

        // A lamp-lit room at night: a warm key from the front-left, a dim
        // violet fill standing in for the dark room around her, and a cool
        // edge from behind so her silhouette does not sink into the floor.
        const key = new THREE.DirectionalLight(0xffc690, 2.3);
        key.position.set(-1.6, 2.4, 2.2);
        const rim = new THREE.DirectionalLight(0x8fa2ff, 0.9);
        rim.position.set(1.8, 1.6, -2.4);
        this.scene.add(key, rim, new THREE.AmbientLight(0xb6a2c8, 0.95));

        this.pivot = new THREE.Group();
        this.pivot.rotation.y = this._yaw;
        this.scene.add(this.pivot);

        const loader = new GLTFLoader();
        loader.register((p) => new VRMLoaderPlugin(p));
        const gltf = await loader.loadAsync(this.base + this.model);
        const vrm = gltf.userData?.vrm;
        if (this._disposed) {
            if (vrm) VRMUtils.deepDispose?.(vrm.scene);
            throw new Error('MindFigure disposed while loading');
        }
        if (!vrm) throw new Error('not a VRM: ' + this.model);
        for (const fn of ['removeUnnecessaryVertices', 'combineSkeletons'])
            if (typeof VRMUtils[fn] === 'function') { try { VRMUtils[fn](gltf.scene); } catch {} }
        this.vrm = vrm;
        // Skinned bounds lag the pose; at this size a culled hand is a whole
        // missing limb.
        vrm.scene.traverse((o) => {
            o.frustumCulled = false;
            for (const m of [].concat(o.material ?? [])) {
                crispTextures(m);
                // MToon's own outline is a second draw of every mesh, sized
                // for a full-screen render; here it is a sub-pixel smear, and
                // the 1px outline below does the job properly. Half the draws.
                if (m.isOutline) m.visible = false;
            }
        });
        // A bigger head is how every sprite in the room keeps a face at 31px
        // tall. Scaled on the RAW bone: the humanoid copies rotations to it
        // every frame but never scale, and the spring bones measure their
        // lengths in world space, so the hair follows.
        const head = vrm.humanoid?.getRawBoneNode('head');
        if (head && this._headScale !== 1) head.scale.multiplyScalar(this._headScale);
        this.pivot.add(vrm.scene);
        if (vrm.lookAt) vrm.lookAt.target = this.camera;

        this.idle = new Idle(vrm);
        this.mixer = new THREE.AnimationMixer(vrm.scene);
        this._features = findFeatures(vrm);

        // Settle: the rest pose is a T-pose, and the first frame out of it
        // flings the hair. A second of simulated time lets the springs land
        // before anyone sees her.
        for (let i = 0; i < 40; i++) this._step(1 / 30);
        this._calibrate();
        this._render(true);

        // Every frame after this one reads back asynchronously (see _render);
        // without WebGL2 fences it falls back to the synchronous read.
        const gl = r.getContext();
        this._async = typeof gl.fenceSync === 'function' && gl.PIXEL_PACK_BUFFER !== undefined;

        this._live = true;
        // Clips arrive in the background; she is already standing and
        // breathing on the procedural layer without them.
        this._clipsDone = this._loadClips().catch((e) =>
            console.warn('[mindfigure] clips unavailable —', e?.message ?? e));
    }

    async _loadClips() {
        let manifest = null;
        try {
            const res = await fetch(this.base + 'clips.json');
            if (res.ok) manifest = await res.json();
        } catch { /* fall back to the built-in file names */ }
        if (this._disposed) return;

        const { loadMixamo } = await import('../universe/vendor/vrm-mixamo/retarget.js');
        for (const c of CLIPS) {
            if (this._disposed) return;
            // Let the room draw a frame between parses.
            await new Promise((res) => (window.requestIdleCallback
                ? requestIdleCallback(res, { timeout: 400 }) : setTimeout(res, 30)));
            if (this._disposed) return;
            const file = manifest?.clips?.find((m) => m.id === c.id)?.file ?? c.file;
            let clip = null;
            try {
                clip = await loadMixamo(this.base + encodeURIComponent(file), this.vrm,
                                        { name: c.id, quiet: true });
            } catch { /* skipped; the procedural layer covers it */ }
            if (this._disposed || !clip) continue;
            const action = this.mixer.clipAction(clip);
            action.setLoop(THREE.LoopRepeat, Infinity);
            action.enabled = true;
            action.setEffectiveWeight(0);
            this._clips.set(c.id, { action, w: 0, want: 0 });
        }
    }

    /**
     * Fit the orthographic frustum to her real silhouette, both facings, in the
     * settled idle pose: soles on the second-to-last row (the last is for the
     * outline under them), the top of her head three rows down (outline plus a
     * little air for a raised hand), the floor point between her feet on the
     * horizontal centre.
     */
    _calibrate() {
        const W = this.width, H = this.height, K = 4;
        const CW = W * K, CH = H * K;
        const cam = this.camera;

        // Provisional frustum, generously larger than she is. Camera-space
        // v = 0 is the aim point, half her height up.
        const span = 2.6, half = (span * CW / CH) / 2;
        cam.left = -half; cam.right = half; cam.top = span / 2; cam.bottom = -span / 2;
        cam.updateProjectionMatrix();
        const cs = span / CH;

        this.renderer.setSize(CW, CH, false);
        const gl = this.renderer.getContext();
        const buf = new Uint8Array(CW * CH * 4);
        let minX = CW, maxX = -1, minY = CH, maxY = -1;
        for (const yaw of [-TURN, TURN]) {
            this.pivot.rotation.y = yaw;
            this.renderer.render(this.scene, cam);
            gl.readPixels(0, 0, CW, CH, gl.RGBA, gl.UNSIGNED_BYTE, buf);
            for (let y = 0; y < CH; y++) {
                for (let x = 0; x < CW; x++) {
                    if (buf[(y * CW + x) * 4 + 3] < 128) continue;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
        }
        this.pivot.rotation.y = this._yaw;
        this.renderer.setSize(W, H, false);
        if (maxX < 0) throw new Error('nothing rendered during calibration');

        // Buffer rows run bottom-up. Camera-space extents of the silhouette:
        const vBottom = cam.bottom + minY * cs;
        const vTop = cam.bottom + (maxY + 1) * cs;
        const uMin = cam.left + minX * cs;
        const uMax = cam.left + (maxX + 1) * cs;

        const sV = (vTop - vBottom) / (H - 4);
        const sU = Math.max(Math.abs(uMin), Math.abs(uMax)) / (W / 2 - 2);
        const s = Math.max(sV, sU);

        let bottom = vBottom - s;
        // The floor point between her feet, in the camera's frame.
        const v0 = new THREE.Vector3(0, 0, 0).applyMatrix4(cam.matrixWorldInverse).y;
        // Snap so the anchor is a whole pixel: the room aligns sprites on whole
        // pixels, and a half-pixel anchor makes her shimmer against the floor.
        // Raising the frustum's bottom slides her DOWN the frame, toward the
        // edge her feet belong on.
        const a = (bottom + H * s - v0) / s;
        const frac = a - Math.floor(a);
        if (frac > 1e-4) bottom += (1 - frac) * s;
        cam.bottom = bottom;
        cam.top = bottom + H * s;
        cam.left = -(W / 2) * s;
        cam.right = (W / 2) * s;
        cam.updateProjectionMatrix();

        this.pixelMetres = s;
        this.anchor = { x: W / 2, y: Math.round((cam.top - v0) / s) };
    }

    // ── public ──────────────────────────────────────────────────────────

    /**
     * Advance and render, at most `fps` times a second. Safe to call every
     * animation frame. Returns true when `canvas` changed.
     * @param {number} [nowMs] a monotonic clock in ms (rAF's timestamp is fine)
     */
    update(nowMs = performance.now()) {
        if (!this._live || this._disposed) return false;
        if (this._lastMs == null || nowMs < this._lastMs) { this._lastMs = nowMs; return false; }
        const since = nowMs - this._lastMs;
        if (since < this._frameMs - 2) return false;
        this._lastMs = nowMs;
        try {
            this._step(Math.min(0.1, since / 1000));
            const changed = this._render();
            this._errors = 0;
            return changed;
        } catch (e) {
            // Never take the room's animation loop down with her. A fault that
            // repeats stops her on her last good frame instead of spamming.
            if (++this._errors === 1) console.warn('[mindfigure] frame failed —', e);
            if (this._errors > 10) this._live = false;
            return false;
        }
    }

    /**
     * @param {'idle'|'talking'|'thinking'|'wave'} s
     * @param {number} [ms] how long to hold it; defaults per state. Calling
     *   'talking' or 'thinking' again while it is running extends it.
     */
    setState(s, ms) {
        if (!STATES.includes(s)) s = 'idle';
        if (s === 'idle') { this.state = 'idle'; this._stateLeft = 0; return; }
        // Asking again for the state she is already in extends it; a new
        // state starts its clip from the top unless it is still fading out
        // from last time (restarting that would snap).
        const clip = this._clips.get(STATE_CLIP[s]);
        if (s !== this.state && clip && !clip.w) clip.action.reset();
        this.state = s;
        this._stateLeft = ms > 0 ? ms / 1000 : HOLD[s];
    }

    /** @param {'sw'|'se'} dir which way she faces in the room */
    setFacing(dir) {
        this.facing = dir === 'se' ? 'se' : 'sw';
    }

    dispose() {
        if (this._disposed) return;
        this._disposed = true;
        this._live = false;
        try { this.mixer?.stopAllAction(); if (this.vrm) this.mixer?.uncacheRoot(this.vrm.scene); } catch {}
        try { if (this.vrm) VRMUtils.deepDispose?.(this.vrm.scene); } catch {}
        this._clips.clear();
        const r = this.renderer;
        if (r) {
            r.domElement.removeEventListener('webglcontextlost', this._onLost);
            r.domElement.removeEventListener('webglcontextrestored', this._onRestored);
            try {
                const gl = r.getContext(), p = this._pending;
                if (p) { gl.deleteSync(p.sync); gl.deleteBuffer(p.pbo); }
            } catch {}
            this._pending = null;
            try { r.renderLists?.dispose(); r.dispose(); r.forceContextLoss(); } catch {}
        }
        this.renderer = this.scene = this.vrm = this.idle = this.mixer = null;
        // The canvas stays valid (drawImage on it must not throw), just empty.
        this._ctx.clearRect(0, 0, this.width, this.height);
        this.frame++;
    }

    // ── per frame ───────────────────────────────────────────────────────

    _step(dt) {
        this._t += dt;
        const t = this._t, vrm = this.vrm;

        if (this.state !== 'idle') {
            this._stateLeft -= dt;
            if (this._stateLeft <= 0) this.state = 'idle';
        }

        // Turn to face the way she is told, quickly but not in one frame.
        const yawWant = this.facing === 'se' ? TURN : -TURN;
        this._yaw += (yawWant - this._yaw) * ease(12, dt);
        this.pivot.rotation.y = this._yaw;

        // 1 — clips. The idle loop is the floor; whichever state clip is live
        // rides over it, and every weight eases so nothing can snap.
        const activeId = STATE_CLIP[this.state] ?? null;
        const k = ease(5.5, dt);
        let over = 0;
        for (const [id, c] of this._clips) {
            if (id === 'idle') continue;
            c.want = id === activeId ? (CLIP_WEIGHT[id] ?? 1) : 0;
            c.w += (c.want - c.w) * k;
            if (c.w < 0.004 && !c.want) c.w = 0;
            over += c.w;
        }
        const norm = over > 1 ? 1 / over : 1;
        over = Math.min(1, over);
        const idleClip = this._clips.get('idle');
        for (const [id, c] of this._clips) {
            const w = id === 'idle' ? 1 - over : c.w * norm;
            if (w <= 0) {
                if (c.action.isRunning()) c.action.stop();
                c.action.setEffectiveWeight(0);
            } else {
                c.action.enabled = true;
                c.action.paused = false;
                c.action.setEffectiveWeight(w);
                if (!c.action.isRunning()) c.action.play();
            }
        }
        this.mixer.update(dt);

        // 2 — procedural layer. Full weight with no idle clip; a whisper of
        // breathing over one. Legs pulled back toward standing (Mixamo idles
        // are braced); arms held clear of the skirt only while nothing else
        // owns them.
        const talking = this.state === 'talking';
        const speech = talking ? this._speech(t) : 0;
        const w = idleClip ? 0.18 : Math.max(0.18, 1 - over);
        this.idle.apply(t, w, speech, dt, 0.82, 0.75 * (1 - over));

        // 3 — fallbacks for a state whose clip never arrived.
        const fk = ease(6, dt);
        const fbWave = this.state === 'wave' && !this._clips.has('wave') ? 1 : 0;
        const fbThink = this.state === 'thinking' && !this._clips.has('idle_think') ? 1 : 0;
        this._fb.wave += (fbWave - this._fb.wave) * fk;
        this._fb.think += (fbThink - this._fb.think) * fk;
        if (this._fb.wave > 0.003) this._waveFallback(t, this._fb.wave);
        if (this._fb.think > 0.003) this._thinkFallback(t, this._fb.think);

        // 4 — face. A mouth that moves with the made-up voice; a smile for a
        // wave, a hint of one while talking; the idle layer's blink on top.
        const em = vrm.expressionManager;
        if (em) {
            const fe = ease(14, dt);
            this._mouth += ((talking ? speech * 0.9 : 0) - this._mouth) * fe;
            const smileWant = this.state === 'wave' ? 0.75 : talking ? 0.2 : 0;
            this._smile += (smileWant - this._smile) * ease(5, dt);
            em.setValue('aa', this._mouth);
            em.setValue('happy', this._smile);
            em.setValue('blink', this.idle.blink * (1 - this._smile * 0.8));
        }

        // 5 — propagate, look-at, springs.
        vrm.update(dt);
    }

    /** A made-up voice envelope: syllables at a few Hz, phrases at slower. */
    _speech(t) {
        const syl = Math.abs(Math.sin(t * 7.3) * Math.sin(t * 2.9 + 1.1));
        const phrase = 0.55 + 0.45 * Math.sin(t * 0.9);
        return clamp01(syl * phrase * 1.15);
    }

    _waveFallback(t, k) {
        const set = (name, x, y, z) => {
            const n = this.vrm.humanoid?.getNormalizedBoneNode(name);
            if (!n) return;
            this._q ??= new THREE.Quaternion();
            this._e ??= new THREE.Euler();
            this._e.set(x, y, z, 'XYZ');
            n.quaternion.slerp(this._q.setFromEuler(this._e), k);
        };
        set('rightUpperArm', 0, 0.35, -0.35);
        set('rightLowerArm', 0, 0, -1.35 + Math.sin(t * 9) * 0.35);
        set('rightHand', 0, 0, 0);
    }

    _thinkFallback(t, k) {
        const head = this.vrm.humanoid?.getNormalizedBoneNode('head');
        if (!head) return;
        this._q2 ??= new THREE.Quaternion();
        this._e2 ??= new THREE.Euler();
        this._e2.set(-0.10 * k, 0.12 * k * Math.sin(t * 0.4), 0.14 * k, 'XYZ');
        head.quaternion.multiply(this._q2.setFromEuler(this._e2));
    }

    /**
     * Draw her and get the pixels back to the CPU.
     *
     * WHY ASYNCHRONOUS. A plain readPixels waits for the GPU to finish the
     * frame — measured at about a millisecond of the main thread doing nothing,
     * every frame, which was a third of her whole cost. Instead the pixels are
     * copied into a pixel-pack buffer with a fence, and collected on the NEXT
     * tick when the fence has long since passed. The price is one tick of
     * latency (~45ms), invisible on a breathing figure. `sync` is for the very
     * first frame, which has to be on the canvas before `ready` resolves.
     *
     * WHY A FRESH BUFFER EACH TIME. Reusing one pack buffer — or ping-ponging
     * two — made Chromium log "READ-usage buffer was written, then fenced, but
     * written again before being read back" on EVERY frame, twenty lines a
     * second into the room's console, even though each was read back first.
     * A buffer written once, read once and deleted (what three's own
     * readRenderTargetPixelsAsync does) is silent. It is 13KB.
     *
     * @returns {boolean} whether `canvas` changed
     */
    _render(sync = false) {
        const r = this.renderer, gl = r.getContext();
        const W = this.width, H = this.height;
        let changed = false;

        const p = this._pending;
        if (p) {
            // Still in flight: leave it, and do not queue another on top.
            if (gl.clientWaitSync(p.sync, 0, 0) === gl.TIMEOUT_EXPIRED) return false;
            gl.deleteSync(p.sync);
            gl.bindBuffer(gl.PIXEL_PACK_BUFFER, p.pbo);
            gl.getBufferSubData(gl.PIXEL_PACK_BUFFER, 0, this._buf);
            gl.bindBuffer(gl.PIXEL_PACK_BUFFER, null);
            gl.deleteBuffer(p.pbo);
            this._pending = null;
            this._compose(p.marks);
            changed = true;
        }

        r.render(this.scene, this.camera);
        // The face marks are measured now, against the pose these pixels are
        // of, and travel with them.
        this._markFlip = !this._markFlip;
        const marks = this._faceMarks(this._markFlip ? this._marksA : this._marksB);

        if (sync || !this._async) {
            gl.readPixels(0, 0, W, H, gl.RGBA, gl.UNSIGNED_BYTE, this._buf);
            this._compose(marks);
            return true;
        }
        const pbo = gl.createBuffer();
        gl.bindBuffer(gl.PIXEL_PACK_BUFFER, pbo);
        gl.bufferData(gl.PIXEL_PACK_BUFFER, W * H * 4, gl.STREAM_READ);
        gl.readPixels(0, 0, W, H, gl.RGBA, gl.UNSIGNED_BYTE, 0);
        gl.bindBuffer(gl.PIXEL_PACK_BUFFER, null);
        const fence = gl.fenceSync(gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
        gl.flush();
        this._pending = { sync: fence, pbo, marks };
        return changed;
    }

    /** Turn raw GL pixels in `_buf` into the sprite on `canvas`. */
    _compose(marks) {
        const W = this.width, H = this.height;
        const src = this._buf;

        // Flip (GL rows run bottom-up), undo premultiplied alpha, and snap
        // alpha to on/off: a pixel sprite has no half-transparent edge.
        const out = this._img.data, mask = this._mask;
        for (let y = 0; y < H; y++) {
            const sRow = (H - 1 - y) * W, dRow = y * W;
            for (let x = 0; x < W; x++) {
                const si = (sRow + x) * 4, di = (dRow + x) * 4, a = src[si + 3];
                if (a < 128) { mask[dRow + x] = 0; out[di + 3] = 0; continue; }
                mask[dRow + x] = 1;
                const m = a === 255 ? 1 : 255 / a;
                out[di] = Math.min(255, src[si] * m);
                out[di + 1] = Math.min(255, src[si + 1] * m);
                out[di + 2] = Math.min(255, src[si + 2] * m);
                out[di + 3] = 255;
            }
        }

        // 1px outline: every empty pixel with a filled 4-neighbour, inked a
        // little toward that neighbour's colour (a "sel-out" — reads softer
        // than flat black against warm hair and skin).
        const ink = this._ink;
        if (ink) {
            for (let y = 0; y < H; y++) {
                for (let x = 0; x < W; x++) {
                    const i = y * W + x;
                    if (mask[i]) continue;
                    let n = -1;
                    if (x > 0 && mask[i - 1]) n = i - 1;
                    else if (x < W - 1 && mask[i + 1]) n = i + 1;
                    else if (y > 0 && mask[i - W]) n = i - W;
                    else if (y < H - 1 && mask[i + W]) n = i + W;
                    if (n < 0) continue;
                    const di = i * 4, ni = n * 4;
                    // 75% ink, 25% the neighbour darkened to 40%.
                    out[di] = ink[0] * 0.75 + out[ni] * 0.1;
                    out[di + 1] = ink[1] * 0.75 + out[ni + 1] * 0.1;
                    out[di + 2] = ink[2] * 0.75 + out[ni + 2] * 0.1;
                    out[di + 3] = 255;
                }
            }
        }

        // Face marks: the eyes (and the mouth while she talks) inked the way
        // the room's sprites have them, a couple of dark pixels. Rendered
        // honestly, a 2px iris is averaged with the white around it into the
        // colour of skin and the face reads as blank — the one thing a figure
        // at this size must not do. Only onto her, never into the air.
        const put = (x, y, c) => {
            if (x < 0 || y < 0 || x >= W || y >= H || !mask[y * W + x]) return;
            const i = (y * W + x) * 4;
            out[i] = c[0]; out[i + 1] = c[1]; out[i + 2] = c[2];
        };
        for (let k = 0, n = marks[0]; k < n; k++) {
            const x = marks[1 + k * 3], y = marks[2 + k * 3], kind = marks[3 + k * 3];
            const px = Math.floor(x);
            if (kind === MARK_MOUTH) { put(px, Math.floor(y), MOUTH_INK); continue; }
            const py = Math.floor(y - 0.5);
            if (kind === MARK_SHUT) put(px, py + 1, EYE_INK);
            else { put(px, py, EYE_INK); put(px, py + 1, EYE_IRIS); }
        }

        this._ctx.putImageData(this._img, 0, 0);
        this.frame++;
    }

    /**
     * Where the face marks go for the pose just rendered, in canvas pixels:
     * `[count, x, y, kind, x, y, kind, ...]`. Empty while her face is turned
     * away from the camera.
     */
    _faceMarks(marks) {
        marks[0] = 0;
        const F = this._features;
        if (!F || (!F.eyes.length && !F.mouth)) return marks;
        const W = this.width, H = this.height;
        const v = (this._fv ??= new THREE.Vector3());

        const head = this.vrm.humanoid?.getNormalizedBoneNode('head');
        if (head) {
            head.getWorldQuaternion(this._hq ??= new THREE.Quaternion());
            v.set(0, 0, 1).applyQuaternion(this._hq);
            if (v.y * Math.sin(ELEVATION) + v.z * Math.cos(ELEVATION) < 0.3) return marks;
        }
        const add = (f, kind) => {
            v.fromBufferAttribute(f.mesh.geometry.attributes.position, f.idx);
            f.mesh.applyBoneTransform(f.idx, v);
            v.applyMatrix4(f.mesh.matrixWorld).project(this.camera);
            const k = marks[0]++;
            marks[1 + k * 3] = (v.x + 1) / 2 * W;
            marks[2 + k * 3] = (1 - v.y) / 2 * H;
            marks[3 + k * 3] = kind;
        };
        const shut = Math.max(this.idle?.blink ?? 0, this._smile) > 0.45;
        for (const e of F.eyes) add(e, shut ? MARK_SHUT : MARK_EYE);
        if (F.mouth && this._mouth > 0.28) add(F.mouth, MARK_MOUTH);
        return marks;
    }
}
