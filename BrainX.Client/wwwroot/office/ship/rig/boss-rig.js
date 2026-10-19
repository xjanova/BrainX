// boss-rig.js — the boss as a cut-out puppet on Canvas 2D.
//
// The painted pieces in boss-parts.{webp,json} hang on a 2D skeleton seen from the room's front
// three-quarter view (facing screen-right; facing left is the same rig mirrored).
//  - Legs are two-bone IK onto feet. While walking a gait phase that advances with DISTANCE
//    travelled places the feet, so a stance foot is locked to the floor and never slides.
//  - The pelvis rides as high as the planted legs reach (inverted pendulum), so walking bobs by itself.
//  - Torso lean, skirt swing, bow wobble and head counter-tilt are damped springs.
//  - The ponytail is a verlet chain drawn as a stack of texture slices along the chain.
//  - Every pose parameter eases toward the current state's target, so states blend instead of popping.
// Units are atlas pixels in a local frame: origin on the floor between the feet, +x = facing, y down.

const PI = Math.PI;
const clamp = (v, a, b) => (v < a ? a : v > b ? b : v);
const lerp = (a, b, t) => a + (b - a) * t;
const smooth = (t) => t * t * (3 - 2 * t);
const damp = (cur, tgt, lambda, dt) => tgt + (cur - tgt) * Math.exp(-lambda * dt);
const dist = (a, b) => Math.hypot(b[0] - a[0], b[1] - a[1]);

/** Verlet rope: point 0 is pinned to an anchor, the rest hang, swing and trail. */
class Rope {
    constructor(n, seg) {
        this.n = n; this.seg = seg; this.p = []; this.q = []; this.ok = false;
        for (let i = 0; i < n; i++) { this.p.push([0, 0]); this.q.push([0, 0]); }
    }
    reset(ax, ay, dx, dy) {
        for (let i = 0; i < this.n; i++) {
            this.p[i][0] = this.q[i][0] = ax + dx * this.seg * i;
            this.p[i][1] = this.q[i][1] = ay + dy * this.seg * i;
        }
        this.ok = true;
    }
    /** Move every point with the world (root motion), so the rope is left behind and trails. */
    shift(dx, dy) { for (let i = 0; i < this.n; i++) { this.p[i][0] += dx; this.p[i][1] += dy; this.q[i][0] += dx; this.q[i][1] += dy; } }
    /** how much of the root motion the hair is left behind by (1 = all of it: it streams like a flag) */
    static TRAIL = 0.25;
    mirror() { for (let i = 0; i < this.n; i++) { this.p[i][0] = -this.p[i][0]; this.q[i][0] = -this.q[i][0]; } }
    update(dt, ax, ay, dx, dy, stiff) {
        if (!this.ok) this.reset(ax, ay, dx, dy);
        const P = this.p, Q = this.q, L = this.seg, n = this.n;
        P[0][0] = Q[0][0] = ax; P[0][1] = Q[0][1] = ay;
        // the first segment leaves the tie along the hair's own direction
        P[1][0] = Q[1][0] = ax + dx * L; P[1][1] = Q[1][1] = ay + dy * L;
        const g = 3800 * dt * dt, drag = Math.exp(-9 * dt);   // lighter and thicker air: slow, soft swings
        const vmax = 650 * dt;                                 // no strand ever whips faster than this (px/s)
        for (let i = 2; i < n; i++) {
            const p = P[i], q = Q[i];
            let vx = (p[0] - q[0]) * drag, vy = (p[1] - q[1]) * drag;
            const sp = Math.hypot(vx, vy);
            if (sp > vmax) { vx *= vmax / sp; vy *= vmax / sp; }
            q[0] = p[0]; q[1] = p[1];
            p[0] += vx; p[1] += vy + g;
        }
        // shape memory: a weak pull toward the hanging rest shape keeps it from folding when she hops
        const ks = 1 - Math.exp(-dt * 3.2);                   // a gentle pull back to the hanging shape
        for (let i = 2; i < n; i++) {
            const tx = ax + dx * L * i * 0.92, ty = ay + dy * L * i;
            P[i][0] += (tx - P[i][0]) * ks; P[i][1] += (ty - P[i][1]) * ks;
        }
        // Bending keeps the ponytail from kinking. It used to move only the current point, 5 times a
        // frame. In Verlet a correction like that becomes velocity, so the hair kept up a ~5 Hz
        // shiver the whole time she walked (worse at 120 Hz). That was the "flick". Now it moves the
        // previous point by the same amount: it shapes the hair without adding energy. It is
        // frame-rate independent.
        const kb = 1 - Math.exp(-dt * stiff);
        for (let i = 2; i < n; i++) {
            const a = P[i - 2], b = P[i - 1], p = P[i], q = Q[i], k = kb * (1 - (i / n) * 0.7);
            const cx = (b[0] * 2 - a[0] - p[0]) * k, cy = (b[1] * 2 - a[1] - p[1]) * k;
            p[0] += cx; p[1] += cy; q[0] += cx; q[1] += cy;
        }
        for (let it = 0; it < 5; it++) {
            for (let i = 2; i < n; i++) {
                const a = P[i - 1], b = P[i], ddx = b[0] - a[0], ddy = b[1] - a[1], d = Math.hypot(ddx, ddy) || 1e-6;
                b[0] = a[0] + (ddx / d) * L; b[1] = a[1] + (ddy / d) * L;
            }
        }
    }
}

/** Damped spring toward a moving target. */
class Spring {
    constructor(k = 90, c = 12) { this.x = 0; this.v = 0; this.k = k; this.c = c; }
    step(target, dt) { this.v += ((target - this.x) * this.k - this.v * this.c) * dt; this.x += this.v * dt; return this.x; }
}

// Pose parameters every state writes. Angles: arm th from straight down, + toward facing; el the
// elbow bend on top of it. Torso/head/pelvis rotation clockwise-positive (canvas convention).
const BASE = {
    sway: 0, drop: 0, pRot: 0, tRot: -0.02, hRot: 0, hNod: 0,
    armN: { th: -0.12, el: 0.10, hand: 0.0 }, armF: { th: 0.10, el: 0.12, hand: 0.0 },
    hipHand: 1,          // near hand on her hip (the boss stance)
    tablet: 0,           // far hand raises the tablet
    stance: 1,           // 1 = feet apart (standing), 0 = feet together
    sit: 0, type: 0,
    hipHandF: 0,         // far hand on her hip too (angry)
    clasp: 0,            // both hands together in front (shy)
    handsUp: 0,          // both hands up by the chest (surprised)
    faceHand: 0,         // far hand up by her face (wink)
    lap: 0,              // near hand resting on her thigh (sitting on the sofa)
    point: 0,            // far hand points with the index finger (giving orders)
    open: 0, openN: 0,   // far / near hand open, palm out, all five fingers showing (wave, cheer)
    talkF: 0, palmF: 0, talkN: 0,   // talking hand shapes: far relaxed open / far palm up / near relaxed open
};

// Face. Each mood is a whole head (swapped), and blinking / talking are patches cut from the same
// sheet that ride on top of whatever head is showing. Moods whose eyes are already shut skip the
// blink; moods whose mouth is already open skip the lip-flap.
const NO_BLINK = new Set(['happy', 'wink']);
// talking gestures: far arm angles (th shoulder, el elbow), the hand shape it takes (f), and for
// some, the near hand leaving her hip to gesture too (n = [th, el])
const TALK = [
    { f: 'talkF', th: 0.55, el: 1.25, hand: -0.25 },               // explaining, open hand up
    { f: 'palmF', th: 0.42, el: 0.98, hand: 0 },                   // presenting, palm up
    { f: 'open', th: 0.85, el: 0.95, hand: 0.1 },                  // "look!", five fingers spread
    { f: 'point', th: 0.72, el: 0.72, hand: -0.2 },                // making a point
    { f: 'palmF', th: 0.38, el: 1.02, hand: 0, n: [0.22, 1.3] },   // both hands: palm up + open near hand
];
const MOUTH_FLAP = new Set(['neutral', 'smirk', 'sad', 'angry', 'shy']);
const STATE_MOOD = { idle: 'smirk', talk: 'neutral', instruct: 'smirk', wave: 'happy', joy: 'happy', sit: 'neutral' };

// Per-character settings. Everything else comes from each character's pieces and their joints.
export const CHARACTERS = {
    boss: {
        files: 'boss',
        hair: { front: false, dir: [-0.22, 0.975] },   // her ponytail hangs behind her
        bust: [255, 165, 120, 85],                     // torso px: centre x, y and radii of the chest
        // what her far hand holds, offset from her far shoulder (standing / seated)
        held: { piece: 'tablet', at: [185, 175], atSit: [150, 235], upK: 1 },
        near: null,                                    // her near hand rests on her hip
    },
    claude: {
        files: 'claude',
        hair: { front: true, dir: [0.05, 1] },         // her braid hangs over her near shoulder, in front
        bust: [212, 178, 74, 72],
        // her sleeves were drawn long: at full length her hands hung to her knees (fingertips
        // belong at mid-thigh). The pieces' elbow ends were cut back past their ribbed cuffs
        // (585 -> 492 px of arm), so this is 0.78 of the old length
        armK: 0.93,
        puffSleeve: true,
        handK: 0.8,
        // the coffee mug, held up by her far shoulder: the upper arm comes toward the camera, so it is
        // foreshortened, and the mug stays upright in her hand
        // (elbow down by her side, forearm up; the mug is drawn over her sleeve, its handle in her hand)
        // It is a prop she picks up later, not part of her stance: she starts empty-handed
        // (`rig.carrying = true` puts it in her hand).
        held: { piece: 'mug', at: [100, -60], atSit: [110, -20], upK: 0.42, upright: true, layer: 'over', carried: false },
        // her laptop, tucked under her near arm with the hand at the side of her hip
        near: { piece: 'laptop', at: [-60, 40], rot: -0.32 },
    },
};

for (const who of ['codex', 'grok', 'cluadex', 'mai']) {
    CHARACTERS[who] = {
        files: who, minimal: true,
        puffSleeve: true, upperLength: 259, foreLength: 162,
        hair: { front: false, dir: [-0.12, 0.993], hidden: who === 'grok' },
        bust: [240, 165, 100, 80],
        held: { piece: 'tablet', at: [185, 175], atSit: [150, 235], upK: 1, carried: false },
        near: null,
    };
}

for(const [who,scale] of Object.entries({boss:1,claude:1,codex:1.23,grok:1.06,cluadex:1.35,mai:1.06}))CHARACTERS[who].headScale=scale;

export class BossRig {
    /** `v` is appended as ?v= so a rebuilt atlas is never served from cache with a stale json */
    static async load(base, v = '', who = 'boss') {
        const cfg = CHARACTERS[who] || CHARACTERS.boss, f = cfg.files;
        const q = v ? '?v=' + encodeURIComponent(v) : '';
        const image = (src) => new Promise((ok, no) => { const i = new Image(); i.onload = () => ok(i); i.onerror = no; i.src = src; });
        const optionalImage = src => cfg.minimal ? Promise.resolve(null) : image(src).catch(() => null);
        const optionalJson = src => cfg.minimal ? Promise.resolve(null) : fetch(src).then(r => r.ok ? r.json() : null).catch(() => null);
        const [img, json, fimg, fjson, frontImg, frontJson, propImg, propJson] = await Promise.all([
            image(base + f + '-parts.webp' + q),
            fetch(base + f + '-parts.json' + q).then((r) => r.json()),
            // the face pack is optional: without it she keeps the single painted head
            optionalImage(base + f + '-faces.webp' + q),
            optionalJson(base + f + '-faces.json' + q),
            // the straight-on frame shown in the middle of a turn (optional too)
            optionalImage(base + f + '-front.webp' + q),
            optionalJson(base + f + '-front.json' + q),
            // props she holds as whole pieces: the hand gripping the tablet (optional)
            optionalImage(base + f + '-props.webp' + q),
            optionalJson(base + f + '-props.json' + q),
        ]);
        // Owner edits from editor.html, all optional: per-pose tweaks (move / scale / turn a piece)
        // and an eraser mask per atlas (alpha = what is kept). Never cached: the editor rewrites them.
        const fresh = '?t=' + Date.now();
        const [tweaks, ...masks] = await Promise.all([
            fetch(base + f + '-tweaks.json' + fresh, { cache: 'no-store' }).then((r) => (r.ok ? r.json() : null)).catch(() => null),
            ...['parts', 'faces', 'props'].map((k) => image(base + f + '-' + k + '-mask.png' + fresh).catch(() => null)),
        ]);
        // every atlas is drawn from a canvas: the mask applied, and readable for the editor's hit test
        const atlases = {};
        const sheet = (key, src) => {
            if (!src) return null;
            const c = document.createElement('canvas'); c.width = src.naturalWidth; c.height = src.naturalHeight;
            const mask = masks[['parts', 'faces', 'props'].indexOf(key)];
            atlases[key] = { orig: src, mask, canvas: c, file: base + f + '-' + key + '-mask.png' };
            BossRig.composite(atlases[key]);
            return c;
        };
        const rig = new BossRig(sheet('parts', img), json, sheet('faces', fimg), fjson, cfg);
        rig.atlases = atlases;
        rig.tweaks = tweaks || {};
        rig.tweakFile = base + f + '-tweaks.json';
        const propSheet = sheet('props', propImg);
        if (propImg && propJson) {
            for (const [k, v] of Object.entries(propJson.parts)) rig.P[k] = { ...v, img: propSheet };
            for (const k of ['tabletHand', 'pointHand', 'openHand', 'peaceHand', 'talkHand', 'palmHand', 'chestHand']) {
                const th = rig.P[k];
                if (th) th.axis = Math.atan2(th.j.knuckle[1] - th.j.wrist[1], th.j.knuckle[0] - th.j.wrist[0]);
            }
        }
        if (frontImg && frontJson) rig.front = { img: frontImg, ...frontJson };
        const [walkImg, walkJson] = await Promise.all([
            image(new URL('./walk-hand.webp', import.meta.url).href + q).catch(() => null),
            fetch(new URL('./walk-hand.json', import.meta.url).href + q).then(r => r.ok ? r.json() : null).catch(() => null),
        ]);
        if (walkImg && walkJson) {
            const j = walkJson.j;
            rig.P.walkHand = { ...walkJson, img: walkImg, axis: Math.atan2(j.knuckle[1] - j.wrist[1], j.knuckle[0] - j.wrist[0]) };
        }
        // A dorsal near-hand is not a substitute for a visible left palm.
        if(rig.P.palmHand)rig.P.farHand={...rig.P.palmHand,scale:f==='claude'?.84:.9,asset:base+f+'-props.webp#palmHand'};
        // Character-specific identity overrides (skin/manicure), never a shared recolor.
        const handManifest=await fetch(base+f+'-hands.json'+q).then(r=>r.ok?r.json():null).catch(()=>null);
        if(handManifest)await Promise.all(['walkHand','farHand'].map(async key=>{
            const p=handManifest[key];if(!p)return;
            const img=await image(base+p.image+q).catch(()=>null);if(!img)return;
            const j=p.j;rig.P[key]={...p,img,asset:base+p.image,axis:Math.atan2(j.knuckle[1]-j.wrist[1],j.knuckle[0]-j.wrist[0])};
        }));
        return rig;
    }

    /** an atlas as drawn: the painted sheet with the owner's eraser mask applied */
    static composite(a) {
        const g = a.canvas.getContext('2d', { willReadFrequently: true });
        g.globalCompositeOperation = 'copy';
        g.drawImage(a.orig, 0, 0);
        if (a.mask) { g.globalCompositeOperation = 'destination-in'; g.drawImage(a.mask, 0, 0); }
        g.globalCompositeOperation = 'source-over';
        a.canvas._rigRevision = (a.canvas._rigRevision || 0) + 1;
    }

    constructor(img, json, faceImg = null, faceJson = null, cfg = CHARACTERS.boss) {
        this.img = img;
        this.cfg = cfg;
        this.P = json.parts;
        this.hasFaces = !!(faceImg && faceJson);
        if (this.hasFaces) {
            for (const [k, v] of Object.entries(faceJson.parts)) this.P[k] = { ...v, img: faceImg };
            this.P.head = this.P.head_neutral;          // its neck/tie joints drive the skeleton
        }
        const P = this.P, len = (k) => dist(P[k].j.a, P[k].j.b);
        // armK: her arms' length against her pieces (the sleeves are drawn squeezed along the arm)
        const ak = cfg.armK || 1;
        this.L = { thigh: len('thigh'), shin: len('shin'), upper: len('upperArm') * ak, fore: len('foreArm') * ak, ankleH: P.foot.j.heel[1] - P.foot.j.a[1] };
        this.L.upper = cfg.upperLength ?? this.L.upper;
        this.L.fore = cfg.foreLength ?? this.L.fore;
        for (const k of ['thigh', 'shin', 'upperArm', 'foreArm', 'foreArmSolid', 'hand']) {
            if (!P[k]) continue;
            const j = P[k].j; P[k].axis = Math.atan2(j.b[1] - j.a[1], j.b[0] - j.a[0]);
        }
        // offsets between pieces, from their own joints
        const T = P.torso.j, S = P.skirt.j, H = P.head.j;
        this.off = {
            neck: [T.neck[0] - T.waist[0], T.neck[1] - T.waist[1]],
            shN: [T.shN[0] - T.waist[0], T.shN[1] - T.waist[1]],
            shF: [T.shF[0] - T.waist[0], T.shF[1] - T.waist[1]],
            hipN: [S.hipN[0] - S.waist[0], S.hipN[1] - S.waist[1]],
            hipF: [S.hipF[0] - S.waist[0], S.hipF[1] - S.waist[1]],
            tie: [H.tie[0] - H.neck[0], H.tie[1] - H.neck[1]],
            cape: T.cape ? [T.cape[0] - T.waist[0], T.cape[1] - T.waist[1]] : null,
        };
        const tailH = P.tail.rect[3] - P.tail.j.tie[1];
        // 14 slices: short enough that a bent ponytail never opens a wedge between two of them
        this.tail = new Rope(15, tailH / 14);
        this.tailSlices = 14;
        // world state
        this.x = 0; this.y = 0; this.F = 1; this.turn = 1; this.Fv = 1;
        this.turnMode = 'instant';
        this.carrying = cfg.held.carried !== false;   // her held prop (tablet, mug) is in her hand
        this.tabletTilt = -0.5;      // extra turn of the held tablet against the forearm, radians (stands it up as in her design)   // 'instant' | 'front' (straight-on middle frame) | 'paper' (squash to a sliver)
        this.target = null; this.speed = 0; this.vx = 0; this.vy = 0;
        this.state = 'idle'; this.stateT = 0; this.t = 0;
        this.pose = structuredClone(BASE);
        this.gait = { phase: 0, moving: false, feet: [{ x: -70, y: 10, up: 0 }, { x: 80, y: -6, up: 0 }] };
        this.sp = { lean: new Spring(70, 11), skirt: new Spring(60, 6), bow: new Spring(55, 5), head: new Spring(80, 11),
            bust: new Spring(260, 4.5) };   // under-damped on purpose: it should bounce
        this.bustGain = 1;   // 0 turns the bounce off
        this.hop = 0; this.hopV = 0;
        this.seat = null;      // {x, y} floor point of the chair when sitting
        this.seatKneeX = 345;  // seated on her lap piece: where her knees come, local x (just past the sofa's front edge)
        this.unitPx = null;    // world px per rig unit (her draw scale), for foot-locked strides; null = old behaviour
        this.maxSpeed = 260;   // walking speed, world px/s
        this.debug = false;
        this.J = null;
        this.moodOverride = null; this.moodHold = 0; this.mood = 'smirk';
        this.blinkNext = 1.5 + Math.random() * 2; this.blinkT = -1;
        this.mouthT = 0; this.mouthShape = null; this.speaking = false;
    }

    /** show a mood for `hold` seconds (Infinity = until changed); null goes back to the state's own */
    /** hold = seconds: a passing reaction (a tap makes her jump), after which the face goes back to
     *  the mood picked before it, or to automatic. Infinity (default) = a mood picked to stay. */
    setMood(m, hold = Infinity) {
        // (a second tap during a reaction keeps the face to go back to from the first one)
        if (hold !== Infinity) this.moodBack = this.moodHold === Infinity ? this.moodOverride : this.moodHold > 0 ? this.moodBack : null;
        else this.moodBack = null;
        this.moodOverride = m; this.moodHold = m ? hold : 0;
        if (m === 'surprised' && this.state !== 'sit') this.hopV = -420;   // a little jump (not off the sofa)
    }

    /** world x under her feet, for the floor shadow (seated, her feet are out in front of her) */
    footX(scale) {
        const J = this.J, L = J && J.lapLegs, k = L ? smooth(clamp(this.pose.sit, 0, 1)) : 0;
        if (!L || k <= 0) return this.x;
        const fx = (L[0].A[0] + L[1].A[0]) / 2;
        return this.x + lerp(0, fx, k) * scale * this.Fv;
    }

    /** is the point (screen px, same frame as x/y) on her body? `scale` as passed to draw() */
    hit(px, py, scale) {
        const dx = (px - this.x) / scale, dy = (py - this.y) / scale;
        const sitDrop = this.state === 'sit' ? 260 : 0;
        return Math.abs(dx) < 210 && dy < 10 && dy > -1620 + sitDrop;
    }

    // -- owner tweaks (editor.html) -------------------------------------------------------------
    // tweaks = { pose: { key: {dx, dy, r, s} } }: per pose, a piece moved (rig px), turned (rad) and
    // scaled. 'all' applies in every pose. Near and far limbs are separate keys (handN, shinF...).
    /** the pose the tweaks are keyed by */
    poseKey() {
        if (this.clip || this.mocap) return 'clip';
        if (this.state === 'sit') return 'sit';
        if (this.gait.moving) return 'walk';
        const m = this.mood;
        if ((this.state === 'idle' || this.state === 'talk') && ['surprised', 'sad', 'angry', 'shy', 'wink', 'tired'].includes(m)) return m;
        return this.state;
    }

    /** the tweak key of a piece: its group (every head / hand shape is one), and the side for limbs */
    tkey(part) {
        let g = part;
        if (g.startsWith('head_') || /^(eyes|mouth)/.test(g)) g = 'head';
        else if (g === 'torsoLogo') g = 'torso';
        else if (g === 'bowLogo') g = 'bow';
        else if (g === 'foreArmSolid') g = 'foreArm';
        else if (g === 'shinSoft' || g === 'knee') g = 'shin';
        else if (g === 'hand' || g.endsWith('Hand')) g = 'hand';
        return ['hand', 'foreArm', 'upperArm', 'thigh', 'shin', 'foot'].includes(g) ? g + (this._side || '') : g;
    }

    /** blend the tweaks of the pose she is in (eased over ~0.3 s, or at once with snap) */
    refreshTweaks(dt = 0, snap = false) {
        const key = this.poseKey(), W = (this.poseW ||= {}), tw = {};
        this.curPose = key;
        for (const k of new Set([...Object.keys(this.tweaks || {}), ...Object.keys(W)])) {
            const want = k === key ? 1 : 0;
            W[k] = snap ? want : damp(W[k] || 0, want, 9, dt);
        }
        const add = (set, w) => {
            for (const [part, v] of Object.entries(set || {})) {
                const o = (tw[part] ||= { dx: 0, dy: 0, r: 0, s: 1, a: 1 });
                o.dx += (v.dx || 0) * w; o.dy += (v.dy || 0) * w; o.r += (v.r || 0) * w; o.s *= 1 + ((v.s ?? 1) - 1) * w;
                o.a *= 1 + ((v.a ?? 1) - 1) * w;
            }
        };
        add(this.tweaks && this.tweaks.all, 1);
        for (const [k, w] of Object.entries(W)) if (k !== 'all' && w > 0.001) add(this.tweaks[k], w);
        this._tw = tw;
    }

    /** a piece's anchor, turn and scale with its tweak applied */
    tf(part, A, rot) {
        const t = this._tw && this._tw[this.tkey(part)];
        const T=t ? { A: [A[0] + t.dx, A[1] + t.dy], rot: rot + t.r, k: t.s, a: t.a } : { A, rot, k: 1, a: 1 };
        if(part==='head'||part.startsWith('head_')||/^(eyes|mouth)/.test(part)||part==='bow'||part==='bowLogo')T.k*=this.cfg.headScale||1;
        return T;
    }

    /** the editor's hit list: every piece drawn this frame, with its transform */
    hitRec(part, p, j) {
        if (!this.hits) return;
        this.hits.push({ part, key: this.tkey(part), img: p.img || this.img, rect: p.rect, j, m: this.ctxNow.getTransform() });
    }

    // -- poses cloned from video (editor.html) ----------------------------------------------
    // A mocap frame is the person's pose as 2D directions, angles from straight down, + toward
    // the way she faces: aN / aF = [upper arm, forearm] of her near (right) and far (left) arm,
    // legs = [[thigh, shin] near, far], t = torso lean, h = head tilt on the torso. A clip is
    // { fps, frames: [frame...] }, played on a loop.
    /** follow one frame live (null stops); legs: also take the legs from it */
    setMocap(frame, legs = true) { this.mocap = frame; this.mocapLegs = legs; if (frame && this.state === 'sit') this.state = 'idle'; }

    /** play a clip from video on a loop (speed 1 = as recorded); null stops */
    playClip(clip, opts = {}) {
        this.clip = clip ? { data: clip, t: 0, speed: opts.speed || 1, loop: opts.loop !== false } : null;
        this.mocapLegs = opts.legs !== false && !!(clip && clip.legs !== false);
        if (!clip) this.mocap = null;
        else { this.target = null; if (this.state === 'sit') this.state = 'idle'; }
    }

    /** a clip's frame at time t, eased between its two neighbours */
    static clipFrame(clip, t) {
        const F = clip.frames, n = F.length;
        if (!n) return null;
        const x = t * clip.fps, i = Math.floor(x), f = x - i;
        const a = F[Math.min(n - 1, Math.max(0, i))], b = F[Math.min(n - 1, Math.max(0, i + 1))];
        const mix = (u, v) => (Array.isArray(u) ? u.map((q, k) => mix(q, v[k])) : u + (v - u) * f);
        return { aN: mix(a.aN, b.aN), aF: mix(a.aF, b.aF), legs: a.legs && b.legs ? mix(a.legs, b.legs) : null, t: mix(a.t, b.t), h: mix(a.h, b.h) };
    }

    /** the frame's leg directions on the solved hips; the body moves up or down so her lower
     *  foot keeps to the floor line (a squat sinks her, a kick lifts one foot) */
    mocapLegsApply(dt) {
        const J = this.J, L = this.L, mc = this.mocap;
        if (!this._mcLegs) this._mcLegs = mc.legs.map((l) => l.slice());
        const floor = Math.max(...J.legs.map((l) => l.A[1]));
        J.legs.forEach((l, i) => {
            const [a, b] = this._mcLegs[i] = this._mcLegs[i].map((v, k) => damp(v, mc.legs[i][k], 14, dt));
            l.K = [l.H[0] + Math.sin(a) * L.thigh, l.H[1] + Math.cos(a) * L.thigh];
            l.A = [l.K[0] + Math.sin(b) * L.shin, l.K[1] + Math.cos(b) * L.shin];
            l.lift = 0;
        });
        const dy = floor - Math.max(...J.legs.map((l) => l.A[1]));
        this._mcDy = this._mcDy === undefined ? dy : damp(this._mcDy, dy, 12, dt);
        const shift = (o) => {
            if (Array.isArray(o)) {
                if (o.length === 2 && typeof o[0] === 'number') { o[1] += this._mcDy; return; }
                o.forEach(shift);
            } else if (o && typeof o === 'object') Object.values(o).forEach(shift);
        };
        for (const [k, v] of Object.entries(J)) if (k !== 'lapLegs') shift(v);
    }

    /** idle | talk | instruct | wave | joy | sit */
    play(state) { this.state = state; this.stateT = 0; this.clip = null; this.mocap = null; if (state === 'joy') this.hopV = -900; }

    /** Walk to a floor point (world px); she stops facing the way she walked. */
    walkTo(x, y) { this.target = [x, y]; if (this.state === 'sit') this.state = 'idle'; }

    // ── pose targets per state ────────────────────────────────────────────────
    targetPose(t) {
        const T = structuredClone(BASE), st = this.state, s = this.stateT, moving = this.gait.moving;
        const breath = Math.sin(t * 2.1);
        T.sway = Math.sin(t * 0.55) * 7;
        T.pRot = Math.sin(t * 0.55) * 0.012;
        T.tRot = -0.02 + breath * 0.008;
        T.hRot = Math.sin(t * 0.43) * 0.03;
        T.armF.th += Math.sin(t * 0.9) * 0.03;
        if (moving) {
            T.hipHand = 0; T.stance = 0; T.sway = 0;
            // walking tall: an upright back, the hips swaying with each step, soft arms
            const lp = this.gait.phase, c = Math.cos(2 * PI * lp);
            const energy = clamp(this.speed / this.maxSpeed, 0, 1), A = 0.20 * energy;
            // Each arm opposes its own leg. Elbows and wrists stay soft, with hands beside thighs.
            T.armN.th = -0.04 - A * c; T.armN.el = 0.12 + 0.06 * Math.max(0, -c); T.armN.hand = -0.05 * c;
            T.armF.th = -0.04 + A * c; T.armF.el = 0.12 + 0.06 * Math.max(0, c); T.armF.hand = 0.05 * c;
            T.sway = 4 * Math.sin(2 * PI * lp);
            T.pRot = 0.016 * Math.sin(2 * PI * lp);
            T.tRot = 0.012 - 0.010 * Math.sin(2 * PI * lp);
            // Two soft weight transfers per stride, with a delayed head nod and small counter-roll.
            // The bob moves the pelvis/torso/neck together; the head never pulls off its neck joint.
            T.drop = 6 * energy * (0.5 + 0.5 * Math.cos(4 * PI * lp - 0.35));
            T.hNod = 0.022 * energy * Math.sin(4 * PI * lp - 0.45);
            T.hRot = -T.tRot * 0.5 + 0.010 * energy * Math.sin(2 * PI * lp - 0.25);
        } else if (st === 'talk') {
            // Talking moves in beats: every 1.4–2.6 s her hand goes to a new gesture, a new shape in a
            // new place (never the same one twice running). The arm eases there with the pose.
            const G = this._talk || (this._talk = { g: 0, until: -1, last: 0 });
            if (s < G.last) G.until = -1;            // talking again: a fresh beat
            G.last = s;
            if (s >= G.until) {
                let n = Math.floor(Math.random() * TALK.length);
                if (n === G.g) n = (n + 1) % TALK.length;
                G.g = n; G.until = s + 1.4 + Math.random() * 1.2;
            }
            const g = TALK[G.g], w1 = Math.sin(t * 3.1), w2 = Math.sin(t * 4.3);
            T.hNod = Math.max(0, Math.sin(t * 5.2)) * 0.05;
            T.hRot = Math.sin(t * 1.7) * 0.05;
            T.armF.th = g.th + w1 * 0.06; T.armF.el = g.el + w2 * 0.12; T.armF.hand = g.hand;
            T[g.f] = 1;
            if (g.n) { T.hipHand = 0; T.armN.th = g.n[0] + w2 * 0.05; T.armN.el = g.n[1] + w1 * 0.1; T.talkN = 1; }
        } else if (st === 'instruct') {
            const k = smooth(clamp(s / 0.35, 0, 1));
            T.armF.th = lerp(0.1, 1.75, k); T.armF.el = lerp(0.12, -0.1, k); T.armF.hand = -0.2; T.point = 1;
            T.tRot = lerp(-0.02, 0.05, k); T.hRot = -0.06 * k + Math.max(0, Math.sin(t * 6)) * 0.03 * k;
        } else if (st === 'wave') {
            const k = smooth(clamp(s / 0.3, 0, 1));
            T.armF.th = lerp(0.1, 2.75, k); T.armF.el = lerp(0.12, 0.45 + Math.sin(t * 11) * 0.38, k); T.armF.hand = 0.2; T.open = 1;
            T.hRot = 0.06 * k;
        } else if (st === 'joy') {
            T.hipHand = 0;
            T.armN.th = -2.6; T.armN.el = -0.3; T.armF.th = 2.6; T.armF.el = 0.3; T.open = 1; T.openN = 1;
            T.hRot = -0.08; T.tRot = -0.06;
            if (s > 1.6) this.play('idle');
        } else if (st === 'sit') {
            // the boss on the sofa: leaning back, one hand on her thigh, reading her tablet
            T.sit = 1; T.hipHand = 0; T.stance = 0; T.sway = 0; T.pRot = 0;
            T.tRot = -0.07 + breath * 0.006; T.hRot = 0.06 + Math.sin(t * 0.4) * 0.02;
            T.type = 0; T.tablet = this.carrying ? 1 : 0; T.lap = 1;
        } else {
            T.tablet = this.carrying ? 1 : 0;
        }
        // the mood's body language, only while she stands still doing nothing else
        const m = this.mood;
        if (!moving && (st === 'idle' || st === 'talk')) {
            if (m === 'sad') { T.hipHand = 0; T.tablet = 0; T.hRot += 0.13; T.tRot += 0.05; T.armN = { th: -0.04, el: 0.04, hand: 0 }; T.armF = { th: 0.04, el: 0.05, hand: 0 }; T.stance = 0.4; T.sway *= 0.4; }
            if (m === 'angry') { T.tablet = 0; T.hipHand = 1; T.hipHandF = 1; T.tRot += 0.07; T.hRot += -0.04 + Math.sin(t * 9) * 0.015; }
            if (m === 'surprised') { T.hipHand = 0; T.tablet = 0; T.handsUp = 1; T.tRot -= 0.09; T.hRot -= 0.05; }
            if (m === 'shy') { T.hipHand = 0; T.tablet = 0; T.clasp = 1; T.hRot += 0.12; T.tRot += 0.03; T.stance = 0; T.sway = Math.sin(t * 1.3) * 10; }
            if (m === 'wink') { T.tablet = 0; T.faceHand = 1; T.hRot -= 0.07; }
            if (m === 'tired') { T.hipHand = 0; T.tablet = 0; T.tRot += 0.09; T.hRot += 0.16 + Math.sin(t * 0.8) * 0.03; T.armN = { th: -0.02, el: 0.03, hand: 0 }; T.armF = { th: 0.03, el: 0.04, hand: 0 }; T.stance = 0.5; T.sway = Math.sin(t * 0.35) * 5; }
            if (m === 'happy') { T.hRot -= 0.05; }
        }
        const mc = this.mocap;
        if (mc) {
            for (const k of ['hipHand', 'tablet', 'hipHandF', 'clasp', 'handsUp', 'faceHand', 'lap', 'point', 'open', 'openN', 'talkF', 'palmF', 'talkN', 'type', 'sit']) T[k] = 0;
            const el = (a) => { let d = a[1] - a[0]; while (d > PI) d -= 2 * PI; while (d < -PI) d += 2 * PI; return d; };
            T.armN = { th: mc.aN[0], el: el(mc.aN), hand: 0 };
            T.armF = { th: mc.aF[0], el: el(mc.aF), hand: 0 };
            T.tRot = clamp(mc.t, -0.6, 0.6); T.hRot = clamp(mc.h, -0.5, 0.5);
            T.sway = 0; T.stance = 1; T.drop = 0; T.pRot = 0;
        }
        return T;
    }

    /** blink and lip-flap timers */
    updateFace(dt) {
        if (this.moodHold > 0) {
            this.moodHold -= dt;
            if (this.moodHold <= 0) { this.moodOverride = this.moodBack || null; this.moodHold = this.moodOverride ? Infinity : 0; this.moodBack = null; }
        }
        const mood = this.moodOverride || (this.gait.moving ? 'neutral' : STATE_MOOD[this.state] || 'neutral');
        this.mood = this.P['head_' + mood] ? mood : 'neutral';
        this.blinkNext -= dt;
        if (this.blinkT >= 0) { this.blinkT += dt; if (this.blinkT > 0.2) this.blinkT = -1; }
        if (this.blinkNext <= 0) {
            this.blinkT = 0;
            // now and then a quick double blink
            this.blinkNext = Math.random() < 0.18 ? 0.32 : 2.2 + Math.random() * 3.8;
        }
        const talking = this.state === 'talk' || this.speaking;
        if (talking && MOUTH_FLAP.has(this.mood)) {
            this.mouthT -= dt;
            if (this.mouthT <= 0) {
                const r = Math.random();
                this.mouthShape = r < 0.42 ? 'mouthA' : r < 0.7 ? 'mouthO' : null;
                this.mouthT = 0.07 + Math.random() * 0.09;
            }
        } else this.mouthShape = null;
    }

    /** which eye patch shows this frame (null = the head's own eyes) */
    eyePatch() {
        if (!this.hasFaces || NO_BLINK.has(this.mood) || this.blinkT < 0) return null;
        const b = this.blinkT;
        if (this.mood === 'tired') return b < 0.16 ? 'eyesClosed' : null;
        return b < 0.04 || b > 0.15 ? 'eyesTired' : 'eyesClosed';
    }

    // ── update ────────────────────────────────────────────────────────────────
    update(dt) {
        dt = Math.min(dt, 1 / 20);
        if (!(dt > 0)) return;
        this.t += dt; this.stateT += dt;
        this.updateFace(dt);
        if (this.clip) {
            const c = this.clip, dur = c.data.frames.length / c.data.fps;
            c.t += dt * c.speed;
            if (c.t >= dur) { if (c.loop) c.t %= dur; else { this.clip = null; this.mocap = null; } }
            if (this.clip) this.mocap = BossRig.clipFrame(c.data, c.t);
        }
        this.refreshTweaks(dt);
        const t = this.t, L = this.L, g = this.gait;

        // root motion toward the walk target
        let vF = 0, mx = 0, my = 0;
        if (this.target) {
            const dx = this.target[0] - this.x, dy = this.target[1] - this.y, d = Math.hypot(dx, dy);
            if (d < 6) { this.target = null; }
            else {
                const want = Math.min(this.maxSpeed, d * 3);
                this.speed = damp(this.speed, want, 6, dt);
                mx = (dx / d) * this.speed * dt; my = (dy / d) * this.speed * dt;
                if (Math.abs(dx) > 4) { const f = dx > 0 ? 1 : -1; if (f !== this.F) this.F = f; }
            }
        }
        if (!this.target) this.speed = damp(this.speed, 0, 10, dt);
        this.x += mx; this.y += my;
        vF = this.speed;
        // Turning: the 3/4 rig narrows a little, the straight-on frame takes over through the middle
        // (that is where the facing flips, out of sight), then the rig widens out facing the other
        // way. Without the front frame it falls back to squashing to a sliver.
        // 'instant' (default) flips on the spot: no in-between frame to read as a thin side, and the
        // ponytail and skirt springs swing through on their own. 'front' / 'paper' animate the turn.
        // (an instant turn flips the ponytail WITH her: mirroring it to keep its place in the room
        // left it in front of her face, and it whipped back across her head)
        if (this.F !== this.Fv && this.turnMode === 'instant') { this.Fv = this.F; this.turn = 1; }
        if (this.F !== this.Fv && this.turn >= 1) this.turn = 0;
        if (this.turn < 1) {
            this.turn = Math.min(1, this.turn + dt / (this.front && this.turnMode === 'front' ? 0.34 : 0.18));
            if (this.turn >= 0.5 && this.Fv !== this.F) { this.Fv = this.F; this.tail.mirror(); }
        }
        // root motion in the local (facing) frame drags the hair behind
        // (eased: walking off, stopping or turning round, the hair drifts back over ~half a second
        // instead of being yanked the moment her speed changes)
        if (!this._trail) this._trail = [0, 0];
        const ivx = dt > 0 ? (mx * this.Fv) / dt : 0, ivy = dt > 0 ? my / dt : 0;
        this._trail[0] = damp(this._trail[0], ivx, 2.5, dt); this._trail[1] = damp(this._trail[1], ivy, 2.5, dt);
        this.tail.shift(-this._trail[0] * dt * Rope.TRAIL, -this._trail[1] * dt * Rope.TRAIL);

        // gait
        const moving = vF > 12;
        // the stride is in rig units: when the host says how many world px one rig unit is drawn at
        // (unitPx), her world speed is turned into rig units, so a planted foot stays put on the floor
        const vG = this.unitPx ? vF / this.unitPx : vF;
        // A woman's walk, not a march: a steady cadence (just under one stride a second) with short
        // steps, so walking slower shortens the steps instead of slowing the legs down.
        const cad = clamp(0.72 + vG / 5000, 0.72, 1.02);
        const C = Math.max(160, vG / cad), beta = 0.64, lift = 32 * clamp(vG / 450, 0.5, 1);
        if (moving && !g.moving) g.phase = 0.25;
        g.moving = moving;
        const pz = this.pose;
        const stanceN = [lerp(-14, -70, pz.stance), lerp(10, 12, pz.stance)], stanceF = [lerp(18, 80, pz.stance), lerp(-6, -8, pz.stance)];
        if (moving) g.phase = (g.phase + (vG * dt) / C) % 1;
        this._stride = C;
        for (let i = 0; i < 2; i++) {
            const ft = g.feet[i], base = i ? stanceF : stanceN;
            let fx, up = 0, pitch = 0;
            if (moving) {
                const lp = (g.phase + i * 0.5) % 1;
                if (lp < beta) {
                    fx = C * beta * (0.5 - lp / beta);
                    pitch = lp < 0.10 ? -0.15 * (1 - smooth(lp / 0.10)) : lp > 0.49 ? 0.22 * smooth((lp - 0.49) / (beta - 0.49)) : 0;
                } else {
                    const s = (lp - beta) / (1 - beta);
                    fx = C * beta * (-0.5 + smooth(s)); up = lift * Math.sin(PI * s);
                    pitch = lerp(0.22, -0.15, smooth(s));
                }
                // each foot steps in line with its own hip, not about the body's centre: from the
                // centre the near leg had to reach further forward than back and the far leg the other
                // way round, so the pelvis rose and fell unevenly — a limp
                const hip = i ? this.off.hipF : this.off.hipN;
                fx += hip[0];
            } else if (pz.sit > 0.5) {
                fx = (i ? 150 : 95); up = 0;
            } else {
                fx = base[0];
            }
            const k = moving ? 1 : 1 - Math.exp(-dt * 9);
            // Each foot keeps its own floor line through stance; only the swing lifts it.
            const fy = base[1];
            ft.x += (fx - ft.x) * k; ft.y += (fy - ft.y) * (moving ? 1 : 1 - Math.exp(-dt * 9)); ft.up = moving ? up : damp(ft.up, 0, 12, dt);
            ft.pitch = moving ? pitch : damp(ft.pitch || 0, 0, 12, dt);
        }

        // ease the pose toward the state's target
        const T = this.targetPose(t), lam = 9;
        for (const k of ['sway', 'drop', 'pRot', 'tRot', 'hRot', 'hNod', 'hipHand', 'tablet', 'stance', 'sit', 'type', 'hipHandF', 'clasp', 'handsUp', 'faceHand', 'lap', 'point', 'open', 'openN', 'talkF', 'palmF', 'talkN']) pz[k] = damp(pz[k], T[k], k === 'sit' ? 4 : lam, dt);
        for (const a of ['armN', 'armF']) for (const k of ['th', 'el', 'hand']) pz[a][k] = damp(pz[a][k], T[a][k], moving ? 14 : lam, dt);

        // hop (joy)
        this.hopV += 3200 * dt; this.hop += this.hopV * dt;
        if (this.hop > 0) { this.hop = 0; this.hopV = this.state === 'joy' && this.stateT < 1.2 ? -820 : 0; }

        this.solve(t, dt, vF);
        if (this.mocap && this.mocapLegs && this.mocap.legs) this.mocapLegsApply(dt);
        // springs fed by speed / acceleration
        const acc = (vF - (this._vPrev || 0)) / Math.max(dt, 1e-3); this._vPrev = vF;
        this.sp.lean.step(clamp(acc * 0.0004, -0.08, 0.08), dt);
        this.sp.skirt.step(clamp(-acc * 0.0006, -0.18, 0.18) - vF * 0.0003 + this.hopV * 0.00008, dt);
        this.sp.bow.step(clamp(-acc * 0.0008, -0.3, 0.3) + this.hopV * 0.0002 + Math.sin(t * 1.3) * 0.02, dt);

        // chest: lags the torso's vertical acceleration (each footfall, a hop), then settles
        {
            // the pelvis has a kink each time the stance foot changes; smooth its height before
            // differentiating twice, or every footfall kicks the chest to the limit
            const raw = this.J.waist[1];
            this._wyS = this._wyS === undefined ? raw : damp(this._wyS, raw, 25, dt);
            const wy = this._wyS;
            const v = this._wy === undefined ? 0 : (wy - this._wy) / dt;
            const a = this._wv === undefined ? 0 : (v - this._wv) / dt;
            this._wy = wy; this._wv = v;
            this.sp.bust.step(clamp(-a * 0.011, -16, 16) * this.bustGain, dt);
            this.sp.bust.x = clamp(this.sp.bust.x, -40, 40);   // safety only; the soft limit is in draw()
        }
        // hair: the ponytail's swing is one slow, soft pendulum (~0.6 Hz, gently damped) about the
        // tie, driven by how she moves: walking it leans back a little and sways with her steps,
        // walking off or stopping it drifts back or forward and settles. The rope only adds the
        // bend along the hair.
        const sw = this._sw || (this._sw = { th: 0, w: 0 });
        {
            const vLoc = this._trail[0] / (this._scale || 0.5);           // forward speed, rig px/s
            const stepK = smooth(clamp((vF - 12) / 60, 0, 1));
            const th0 = 0.14 * clamp(vLoc / 700, -1, 1) + 0.035 * stepK * Math.sin(this.gait.phase * PI * 4);
            const w0 = PI * 2 * 0.6, zeta = 0.42;
            sw.w += (w0 * w0 * (th0 - sw.th) - 2 * zeta * w0 * sw.w) * dt;
            sw.th = clamp(sw.th + sw.w * dt, -0.35, 0.35);
        }
        const J = this.J, hairDir = this.rot(this.cfg.hair.dir, sw.th);
        // the head bobs every step; feed the hair a softened copy of the tie so the bob reaches the tips
        // as a slow sway instead of a flick
        if (!this._tieS) this._tieS = [J.tie[0], J.tie[1]];
        this._tieS[0] = damp(this._tieS[0], J.tie[0], 14, dt); this._tieS[1] = damp(this._tieS[1], J.tie[1], 9, dt);
        this.tail.update(dt, this._tieS[0], this._tieS[1], hairDir[0], hairDir[1], 10);   // bending rate, 1/s
    }

    /** two-bone IK: from H toward T, bend sign picks the knee/elbow side */
    ik(H, Tg, l1, l2, bend) {
        let dx = Tg[0] - H[0], dy = Tg[1] - H[1], d = Math.hypot(dx, dy);
        const max = (l1 + l2) * 0.999, min = Math.abs(l1 - l2) + 1;
        if (d > max) { dx *= max / d; dy *= max / d; d = max; }
        if (d < min) { const k = min / (d || 1); dx *= k; dy *= k; d = min; }
        const base = Math.atan2(dy, dx), cosA = clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1, 1);
        const a = base - bend * Math.acos(cosA);
        return { K: [H[0] + Math.cos(a) * l1, H[1] + Math.sin(a) * l1], E: [H[0] + dx, H[1] + dy] };
    }

    rot(v, a) { const c = Math.cos(a), s = Math.sin(a); return [v[0] * c - v[1] * s, v[0] * s + v[1] * c]; }

    /** joint positions for this frame, local frame */
    solve(t, dt, vF) {
        const pz = this.pose, L = this.L, g = this.gait, o = this.off;
        // Roll about the heel/toe contact, not the ankle, so soles neither slide nor sink.
        const roll = g.feet.map(f => {
            const j = this.P.foot.j, p = (f.pitch || 0) > 0 ? j.toe : j.heel;
            const v = [p[0] - j.a[0], p[1] - j.a[1]], r = this.rot(v, f.pitch || 0);
            return [v[0] - r[0], v[1] - r[1]];
        });
        const feet = g.feet.map((f, i) => [f.x + roll[i][0], f.y - L.ankleH - f.up + roll[i][1]]);
        // Walking, every foot's height on the screen from depth (the far foot further back, a foot
        // reaching forward nearer) is foreshortening, not a step up or down: the pelvis is worked out
        // from each foot as if on one flat floor line, and the leg is drawn shorter or longer to reach
        // where the foot really is. Otherwise the far leg, its foot 16 px higher, lifted her hips ~35 px
        // every other step: a limp.
        const walkK = clamp(1 - (this.standK ?? 1), 0, 1) * (1 - smooth(clamp(pz.sit, 0, 1)));
        const yRef = lerp(10, 12, pz.stance);
        const feetV = g.feet.map((f, i) => [f.x + roll[i][0], lerp(f.y, yRef, walkK) - L.ankleH - f.up + roll[i][1]]);
        const R = L.thigh + L.shin;
        // pelvis: as high as the stance legs reach
        const sitK = smooth(clamp(pz.sit, 0, 1));
        // Seated with the lap piece, the pelvis goes where her shins, hanging straight down from the
        // lap's knees, just reach the floor, with the knees at seatKneeX.
        const lap = this.P.lap;
        const lapSeat = lap ? [this.seatKneeX - (lap.j.kneeN[0] - lap.j.waist[0]),
            g.feet[0].y - L.ankleH - L.shin - (lap.j.kneeN[1] - lap.j.waist[1])] : null;
        // walking, the pelvis rides forward a little (into the leading leg) and may only drop a
        // little: a leg that cannot reach its foot then lifts that heel instead (the rear foot
        // pushing off), where before her hips sank ~100 px into every step and she limped
        this._lean = damp(this._lean ?? 0, g.moving ? 0.03 * Math.min(900, this._stride || 0) : 0, 6, dt);
        const pelvisX = lerp(pz.sway + this._lean, lapSeat ? lapSeat[0] : -20, sitK);
        // standing still the legs are all but straight; walking keeps some give in the knees
        this.standK = damp(this.standK ?? 1, g.moving ? 0 : 1, 6, dt);
        const standK = this.standK * (1 - sitK), ext = lerp(0.985, 0.996, standK);
        // The pelvis rides as high as the legs that carry her allow. Walking, that is a planted foot
        // that is not far behind her: the rear foot of a double stance lifts its heel and pushes off
        // instead of pulling her hips down, so the front leg lands straight, as a real step does.
        // (walking, the legs carry her nearly straight: at 97.5% her knees stayed bent ~25° all the
        // way through the step and she walked crouched)
        let hipY = -(L.ankleH + R * (g.moving ? 0.992 : 0.975)) - o.hipN[1], carried = false;
        const stride = (this._stride || 0) * 0.62;
        const dxs = [0, 1].map((i) => feetV[i][0] - (pelvisX + (i ? o.hipF : o.hipN)[0]));
        const planted = [0, 1].filter((i) => !g.moving || g.feet[i].up <= 2);
        // with both feet down, the one far behind is pushing off and does not hold her up
        const holds = planted.length === 2 && g.moving ? planted.filter((i) => dxs[i] >= -0.22 * stride) : planted;
        for (const i of (holds.length ? holds : planted)) {
            const hip = i ? o.hipF : o.hipN, ft = feetV[i], dx = dxs[i];
            const reach = Math.sqrt(Math.max(0, (R * ext) ** 2 - dx * dx));
            hipY = Math.max(hipY, ft[1] - reach - hip[1]); carried = true;
        }
        if (g.moving) hipY = Math.min(hipY, -(L.ankleH + R * ext) - o.hipN[1] + (carried ? 30 : 10));
        // and smoothly: the pelvis is a heavy thing, it never jumps at a heel strike
        this._hipY = g.moving && this._hipY !== undefined ? damp(this._hipY, hipY, 16, dt) : hipY;
        hipY = this._hipY;
        const seatY = -(L.ankleH + L.shin * 0.97) - o.hipN[1] + 10;
        const pelvis = [pelvisX, lerp(hipY, lapSeat ? lapSeat[1] : seatY, sitK) + pz.drop + this.hop + Math.sin(t * 2.1) * 1.5 * (1 - sitK)];
        const pRot = pz.pRot;
        const J = this.J = { pelvis, pRot };
        J.hips = [o.hipN, o.hipF].map((h) => { const r = this.rot(h, pRot); return [pelvis[0] + r[0], pelvis[1] + r[1]]; });
        // legs; seated thighs point at the camera, so they are drawn foreshortened
        const fsh = lerp(1, 0.42, sitK);
        J.legs = feet.map((ft, i) => {
            const H = J.hips[i];
            // In the 3/4 view her far foot stands further back, so higher on the screen. The
            // pelvis is set by the leg that reaches lower, so the far leg has room to spare. Bending
            // it to take up that room left her knock-kneed. Standing, shorten it instead: it does
            // reach back into the room, so it is foreshortened.
            const d = dist(H, ft), dv = dist(H, feetV[i]);
            const s = lerp(1, clamp(d / (R * ext), 0.92, 1), standK) * lerp(1, clamp(d / Math.max(1, dv), 0.9, 1.1), walkK);
            const r = this.ik(H, ft, L.thigh * fsh * s, L.shin * s, 1);
            return { H, K: r.K, A: r.E, fsh, pitch: g.feet[i].pitch || 0, lift: Math.max(0, ft[1] - r.E[1]) };
        });
        // seated: the shins hang from the lap piece's knees
        if (lap) {
            J.lapLegs = ['kneeN', 'kneeF'].map((jn) => {
                const o = this.rot([lap.j[jn][0] - lap.j.waist[0], lap.j[jn][1] - lap.j.waist[1]], pRot);
                const K = [pelvis[0] + o[0], pelvis[1] + o[1]];
                return { K, A: [K[0], K[1] + L.shin] };
            });
        }
        // torso
        const tRot = pz.tRot + this.sp.lean.x;
        J.tRot = tRot;
        J.waist = [pelvis[0], pelvis[1] + 6];
        const at = (off) => { const r = this.rot(off, tRot); return [J.waist[0] + r[0], J.waist[1] + r[1]]; };
        J.neck = at(o.neck); J.shN = at(o.shN); J.shF = at(o.shF);
        const breath = Math.sin(t * 2.1);
        J.shN[1] += breath * 1.2; J.shF[1] += breath * 1.2;
        J.hRot = tRot * 0.4 + pz.hRot + pz.hNod + this.sp.head.step(-tRot * 0.5, dt);
        const tie = this.rot(o.tie.map(v=>v*(this.cfg.headScale||1)), J.hRot);
        J.tie = [J.neck[0] + tie[0], J.neck[1] + tie[1]];
        // arms (FK), with IK overrides: near hand on the hip, typing hands on the keys
        const arm = (S, a) => {
            const E = [S[0] + Math.sin(a.th) * L.upper, S[1] + Math.cos(a.th) * L.upper];
            const W = [E[0] + Math.sin(a.th + a.el) * L.fore, E[1] + Math.cos(a.th + a.el) * L.fore];
            return { S, E, W, hand: a.hand };
        };
        // upK < 1: the upper arm points at the camera, so it is foreshortened
        const blendIK = (A, target, k, bend, upK = 1) => {
            if (k < 0.01) return A;
            const r = this.ik(A.S, target, L.upper * lerp(1, upK, k), L.fore, bend);
            return { S: A.S, E: [lerp(A.E[0], r.K[0], k), lerp(A.E[1], r.K[1], k)], W: [lerp(A.W[0], r.E[0], k), lerp(A.W[1], r.E[1], k)], hand: A.hand };
        };
        let aN = arm(J.shN, pz.armN), aF = arm(J.shF, pz.armF);
        const nh = this.cfg.near;
        aN = blendIK(aN, nh ? [J.hips[0][0] + nh.at[0], J.hips[0][1] + nh.at[1]] : [J.hips[0][0] + 18, J.hips[0][1] - 95], pz.hipHand, -1);
        const tabletK = pz.tablet;
        // the tablet is held out by her side, clear of her chest and the wordmark
        // (seated, lower and in front of her, as if reading it)
        const sitT = smooth(clamp(pz.sit, 0, 1));
        const hd = this.cfg.held;
        aF = blendIK(aF, [J.shF[0] + lerp(hd.at[0], hd.atSit[0], sitT), J.shF[1] + lerp(hd.at[1], hd.atSit[1], sitT)], tabletK, -1, hd.upK);
        // seated: the near hand rests on her thigh, a little above the knee
        const lg = J.legs[0];
        let lapHand = [lerp(lg.H[0], lg.K[0], 0.62), lerp(lg.H[1], lg.K[1], 0.62) - 28];
        if (lap) { const o = this.rot([lap.j.handN[0] - lap.j.waist[0], lap.j.handN[1] - lap.j.waist[1]], J.pRot); lapHand = [J.pelvis[0] + o[0], J.pelvis[1] + o[1]]; }
        aN = blendIK(aN, lapHand, pz.lap, -1);
        // mood hands
        aF = blendIK(aF, [J.hips[1][0] - 20, J.hips[1][1] - 95], pz.hipHandF, 1);
        const front = [J.waist[0] + 70, J.waist[1] - 45];
        aN = blendIK(aN, front, pz.clasp, -1);
        aF = blendIK(aF, [front[0] + 14, front[1] - 6], pz.clasp, 1);
        // surprised, hands to her chest: the elbows stay tucked at her sides (the near one out to the
        // left, not folded in across her belly) and the upper arms come a little toward us, so they
        // are foreshortened and the elbows sit at the ribs, not down at the waist
        aN = blendIK(aN, [J.neck[0] - 20, J.neck[1] + 150], pz.handsUp, -1);
        aF = blendIK(aF, [J.neck[0] + 95, J.neck[1] + 140], pz.handsUp, 1);
        // IK folds a hand this close to the shoulder with the elbow swung in front of her belly. Put
        // the elbow where a girl keeps it, hanging under the shoulder by her ribs, and let the
        // forearm (drawn stretched or squashed between elbow and wrist) foreshorten to reach the hand.
        if (pz.handsUp > 0.01) {
            const tuck = (A, ox) => {
                const E = [A.S[0] + ox, A.S[1] + L.upper * 0.8], k = smooth(clamp(pz.handsUp, 0, 1));
                return { ...A, E: [lerp(A.E[0], E[0], k), lerp(A.E[1], E[1], k)] };
            };
            aN = tuck(aN, -30); aF = tuck(aF, 30);
        }
        // wink: a peace sign beside her far eye, fingers up. The forearm can only stand up under
        // the hand if the upper arm comes toward the camera (elbow forward and down), so the upper
        // arm is foreshortened to 45%. At full length the elbow was forced up to head height and
        // the hand lay across her face.
        aF = blendIK(aF, [J.neck[0] + 165, J.neck[1] - 51], pz.faceHand, -1, 0.45);
        const typeK = pz.type * sitK;
        if (typeK > 0.01) {
            const tap = (ph) => Math.max(0, Math.sin(t * 13 + ph)) * 9;
            aN = blendIK(aN, [J.waist[0] + 300, J.waist[1] - 90 - tap(0)], typeK, -1);
            aF = blendIK(aF, [J.waist[0] + 380, J.waist[1] - 110 - tap(1.7)], typeK, -1);
        }
        J.armN = aN; J.armF = aF;
        return J;
    }

    // ── draw ──────────────────────────────────────────────────────────────────
    /** a piece with its joint `jn` at point A, rotated by `rot`, optionally scaled */
    piece(ctx, part, jn, A, rot, sx = 1, sy = 1, alpha = 1, tint = null) {
        const p = this.P[part], [rx, ry, w, h] = p.rect, j = p.j[jn];
        const T = this.tf(part, A, rot);
        ctx.save();
        ctx.translate(T.A[0], T.A[1]); ctx.rotate(T.rot); ctx.scale(sx * T.k, sy * T.k);
        if (alpha * T.a !== 1) ctx.globalAlpha *= alpha * T.a;
        if (tint) ctx.filter = tint;
        ctx.drawImage(p.img || this.img, rx, ry, w, h, -j[0], -j[1], w, h);
        if (ctx.globalAlpha > 0.3) this.hitRec(part, p, j);
        ctx.restore();
    }

    /** a limb piece spanning A -> B (its joints a -> b), stretched/squashed along its axis */
    limb(ctx, part, A, B, tint, lenScale = null, widthK = 1) {
        const p = this.P[part], ab = dist(p.j.a, p.j.b);
        // a tweak moves both ends, turns the limb about its top joint and scales its width
        const t = this._tw && this._tw[this.tkey(part)];
        if (t) {
            const v = this.rot([B[0] - A[0], B[1] - A[1]], t.r);
            A = [A[0] + t.dx, A[1] + t.dy]; B = [A[0] + v[0], A[1] + v[1]]; widthK *= t.s;
        }
        const fade = t ? t.a : 1;
        const rot = Math.atan2(B[1] - A[1], B[0] - A[0]) - p.axis;
        const k = lenScale ?? 1;
        // stretch along the piece's own axis: rotate into it, scale, rotate back
        ctx.save();
        ctx.translate(A[0], A[1]); ctx.rotate(rot + p.axis); ctx.scale(k === 1 ? dist(A, B) / ab : k, widthK); ctx.rotate(-p.axis);
        if (fade !== 1) ctx.globalAlpha *= fade;
        if (tint) ctx.filter = tint;
        const [rx, ry, w, h] = p.rect, j = p.j.a;
        if (p.blend) {
            // Soft overlaps belong to the puppet's skinning, not to its painted artwork.
            // Cache once per limb: hollow cap rims disappear inside the adjoining fabric.
            const revision=(p.img||this.img)._rigRevision||0;
            if (!p._blended || p._blendRevision!==revision) {
                const c = document.createElement('canvas'); c.width=w;c.height=h;
                const g=c.getContext('2d');g.drawImage(p.img||this.img,rx,ry,w,h,0,0,w,h);
                g.globalCompositeOperation='destination-in';
                if(p.blend.top){const grad=g.createLinearGradient(0,0,0,p.blend.top);grad.addColorStop(0,'transparent');grad.addColorStop(.4,'transparent');grad.addColorStop(1,'white');g.fillStyle=grad;g.fillRect(0,0,w,h);}
                if(p.blend.bottom){const grad=g.createLinearGradient(0,h-p.blend.bottom,0,h);grad.addColorStop(0,'white');grad.addColorStop(1,'transparent');g.fillStyle=grad;g.fillRect(0,0,w,h);}
                p._blended=c;
                p._blendRevision=revision;
            }
            ctx.drawImage(p._blended,-j[0],-j[1]);
        } else ctx.drawImage(p.img || this.img, rx, ry, w, h, -j[0], -j[1], w, h);
        if (ctx.globalAlpha > 0.3) this.hitRec(part, p, j);
        ctx.restore();
    }

    /** a decal riding on a piece (same anchor and rotation) that is NOT mirrored when she faces
     *  left, so the BrainX wordmark always reads the right way round */
    decal(ctx, part, jn, A, rot) {
        const p = this.P[part];
        if (!p) return;
        const [rx, ry, w, h] = p.rect, j = p.j[jn], c = p.j.c;
        const T = this.tf(part, A, rot);
        ctx.save();
        ctx.translate(T.A[0], T.A[1]); ctx.rotate(T.rot); ctx.scale(T.k, T.k);
        if (T.a !== 1) ctx.globalAlpha *= T.a;
        ctx.translate(-j[0] + c[0], -j[1] + c[1]);
        if (this.Fv < 0) {
            // reflect across the line square to the text's own direction: with the rig's mirror that
            // turns the text without flipping it, so a slanted wordmark follows its mirrored ribbon
            const t0 = p.j.t0, t1 = p.j.t1;
            const phi = (t0 && t1 ? Math.atan2(t1[1] - t0[1], t1[0] - t0[0]) : 0) + PI / 2;
            ctx.rotate(phi); ctx.scale(1, -1); ctx.rotate(-phi);
        }
        ctx.drawImage(this.img, rx, ry, w, h, -c[0], -c[1], w, h);
        ctx.restore();
    }

    /**
     * The ponytail as ONE bent sheet: every texture row of the painted tail is laid across the chain
     * at that point (the row's centre column on the chain, the row itself along the chain's normal),
     * and the sheet is drawn as textured triangles. Neighbouring rows share their edge, so a bend
     * never opens a wedge or shows a cut the way stacked rigid slices did.
     */
    drawTail(ctx, rotHead) {
        if (this.cfg.hair.hidden) return;
        const p = this.P.tail, [rx, ry, w, h] = p.rect, tie = p.j.tie, pts = this.tail.p, n = this.tail.n - 1;
        const img = p.img || this.img;
        // the gathered hair above the tie rides on the head
        ctx.save();
        ctx.translate(this.J.tie[0], this.J.tie[1]); ctx.rotate(rotHead);
        ctx.drawImage(img, rx, ry, w, tie[1] + 6, -tie[0], -tie[1], w, tie[1] + 6);
        ctx.restore();
        const segTex = (h - tie[1]) / n;
        // the texture's x axis at each chain point: the head's own at the tie (so the rigid top and
        // the sheet meet without a seam), then square to the chain
        const axis = [];
        for (let i = 0; i <= n; i++) {
            if (i === 0) { axis.push([Math.cos(rotHead), Math.sin(rotHead)]); continue; }
            // a wide window keeps neighbouring rows' axes close, so the inside of a bend cannot fold
            const a = pts[Math.max(0, i - 2)], b = pts[Math.min(n, i + 2)];
            let tx = b[0] - a[0], ty = b[1] - a[1];
            const l = Math.hypot(tx, ty) || 1; tx /= l; ty /= l;
            axis.push([ty, -tx]);
        }
        // which texture column sits on the chain at row y: the hair's own centre line, eased in
        // from the tie column over the first rows so the sheet leaves the rigid top without a step
        const C = p.centre;
        const centreAt = (y, i) => {
            if (!C) return tie[0];
            const f = clamp(y / C.step, 0, C.x.length - 1), k = Math.floor(f), t = f - k;
            const cx = lerp(C.x[k], C.x[Math.min(C.x.length - 1, k + 1)], t);
            return lerp(tie[0], cx, smooth(clamp(i / 3, 0, 1)));
        };
        const L = [], R = [];
        for (let i = 0; i <= n; i++) {
            const c = pts[i], ax = axis[i], cx = centreAt(tie[1] + i * segTex, i);
            L.push([c[0] - ax[0] * cx, c[1] - ax[1] * cx, cx]);
            R.push([c[0] + ax[0] * (w - cx), c[1] + ax[1] * (w - cx)]);
        }
        for (let i = 0; i < n; i++) {
            const y0 = tie[1] + i * segTex, y1 = y0 + segTex;
            this.texTri(ctx, img, rx, ry, w, h, [0, y0], [w, y0], [w, y1], L[i], R[i], R[i + 1]);
            this.texTri(ctx, img, rx, ry, w, h, [0, y0], [w, y1], [0, y1], L[i], R[i + 1], L[i + 1]);
        }
    }

    /** draw texture triangle s0 s1 s2 (piece pixels) onto d0 d1 d2 (current frame), affine */
    texTri(ctx, img, rx, ry, w, h, s0, s1, s2, d0, d1, d2) {
        // grow the clip a hair past each edge, so neighbouring triangles overlap instead of
        // leaving an anti-aliased seam between them
        const cx = (d0[0] + d1[0] + d2[0]) / 3, cy = (d0[1] + d1[1] + d2[1]) / 3, grow = (q) => {
            const dx = q[0] - cx, dy = q[1] - cy, l = Math.hypot(dx, dy) || 1;
            return [q[0] + (dx / l) * 2.5, q[1] + (dy / l) * 2.5];
        };
        const g0 = grow(d0), g1 = grow(d1), g2 = grow(d2);
        // the affine map taking the source triangle onto the destination one
        const x1 = s1[0] - s0[0], y1 = s1[1] - s0[1], x2 = s2[0] - s0[0], y2 = s2[1] - s0[1];
        const u1 = d1[0] - d0[0], v1 = d1[1] - d0[1], u2 = d2[0] - d0[0], v2 = d2[1] - d0[1];
        const det = x1 * y2 - x2 * y1;
        if (Math.abs(det) < 1e-6) return;
        const a = (u1 * y2 - u2 * y1) / det, c = (u2 * x1 - u1 * x2) / det;
        const b = (v1 * y2 - v2 * y1) / det, d = (v2 * x1 - v1 * x2) / det;
        const e = d0[0] - a * s0[0] - c * s0[1], f = d0[1] - b * s0[0] - d * s0[1];
        const top = Math.max(0, Math.min(s0[1], s1[1], s2[1]) - 2), bot = Math.min(h, Math.max(s0[1], s1[1], s2[1]) + 2);
        ctx.save();
        ctx.beginPath(); ctx.moveTo(g0[0], g0[1]); ctx.lineTo(g1[0], g1[1]); ctx.lineTo(g2[0], g2[1]); ctx.closePath();
        ctx.clip();
        ctx.transform(a, b, c, d, e, f);
        ctx.drawImage(img, rx, ry + top, w, bot - top, 0, top, w, bot - top);
        ctx.restore();
    }

    /** chest displacement at torso pixel (x, y): a soft ellipse over the bust, zero at its rim */
    bustDisp(x, y, off) {
        const [BX, BY, RX, RY] = this.cfg.bust;
        const r2 = ((x - BX) / RX) ** 2 + ((y - BY) / RY) ** 2;
        if (r2 >= 1) return [0, 0];
        const t = 1 - r2, w = t * t * (3 - 2 * t);
        return [off * 0.12 * w, off * w];
    }

    /** re-draw the chest region of the torso through a warp grid whose rim stays put, so it covers
     *  the rigid torso under it exactly and only the middle moves */
    drawBust(ctx, A, rot, off) {
        const p = this.P.torso, [rx, ry, w, h] = p.rect, j = p.j.waist, img = p.img || this.img;
        const [BX, BY, RX, RY] = this.cfg.bust, NX = 7, NY = 6;
        const X0 = Math.max(0, BX - RX - 5), X1 = Math.min(w, BX + RX + 3), Y0 = Math.max(0, BY - RY - 15), Y1 = Math.min(h, BY + RY + 5);
        const sx = (i) => X0 + ((X1 - X0) * i) / NX, sy = (k) => Y0 + ((Y1 - Y0) * k) / NY;
        const at = (i, k) => { const x = sx(i), y = sy(k), d = this.bustDisp(x, y, off); return [x + d[0], y + d[1]]; };
        const T = this.tf('torso', A, rot);
        ctx.save();
        ctx.translate(T.A[0], T.A[1]); ctx.rotate(T.rot); ctx.scale(T.k, T.k); ctx.translate(-j[0], -j[1]);
        for (let k = 0; k < NY; k++) {
            for (let i = 0; i < NX; i++) {
                const s00 = [sx(i), sy(k)], s10 = [sx(i + 1), sy(k)], s11 = [sx(i + 1), sy(k + 1)], s01 = [sx(i), sy(k + 1)];
                const d00 = at(i, k), d10 = at(i + 1, k), d11 = at(i + 1, k + 1), d01 = at(i, k + 1);
                this.texTri(ctx, img, rx, ry, w, h, s00, s10, s11, d00, d10, d11);
                this.texTri(ctx, img, rx, ry, w, h, s00, s11, s01, d00, d11, d01);
            }
        }
        ctx.restore();
    }

    /** the straight-on frame, feet on the floor point, as tall as the rig standing (bow included) */
    drawFront(ctx, scale, sx) {
        const f = this.front, J = this.J;
        const bowTop = J.tie[1] - 2 - this.P.bow.j.knot[1];
        const headTop = J.neck[1] - this.P.head.j.neck[1];
        const tall = -Math.min(bowTop, headTop);                 // rig px from the floor to her top
        const k = (tall / (f.feet[1] - f.top)) * scale;
        ctx.scale(k * sx, k);
        ctx.drawImage(f.img, -f.feet[0], -f.feet[1]);
    }

    draw(ctx, scale = 0.3) {
        const J = this.J;
        if (!J) return;
        this._scale = scale;     // the hair's swing reads her speed in rig px
        this.ctxNow = ctx; this._side = '';
        if (this.hits) this.hits.length = 0;
        if (!this._tw) this.refreshTweaks(0, true);
        const far = 'brightness(0.78) saturate(0.9)';
        const farFront = 'brightness(0.93) saturate(0.96)';   // the far forearm and hand when they come in front of her
        ctx.save();
        ctx.translate(this.x, this.y);
        let sq = 1;
        const u = this.turn;
        if (u < 1 && this.front && this.turnMode === 'front') {
            const A = 0.26, B = 0.74;
            if (u > A && u < B) {
                this.drawFront(ctx, scale, 0.82 + 0.18 * Math.sin(PI * (u - A) / (B - A)));
                ctx.restore();
                return;
            }
            sq = u <= A ? 1 - 0.42 * (u / A) : 0.58 + 0.42 * ((u - B) / (1 - B));
        } else if (u < 1) sq = Math.max(0.06, Math.abs(1 - 2 * u));
        ctx.scale(scale * this.Fv * sq, scale);

        if (!this.cfg.hair.front) this.drawTail(ctx, J.hRot);
        // her cardigan's body hangs behind her from the torso, swaying a little with her skirt
        if (this.P.cape && this.off.cape) {
            const o = this.rot(this.off.cape, J.tRot);
            this.piece(ctx, 'cape', 'top', [J.waist[0] + o[0], J.waist[1] + o[1]], J.tRot * 0.6 + this.sp.skirt.x * 0.25);
        }
        // what her far hand holds: the tablet out by her side, or a mug kept upright in her palm
        const heldPiece = (a, handRot, k, tint) => {
            const hd = this.cfg.held;
            if (!this.P[hd.piece]) return;
            ctx.save(); ctx.globalAlpha *= smooth(clamp((k - 0.35) / 0.15, 0, 1));
            if (hd.upright) {
                const ha = handRot + this.P.hand.axis;   // the hand's own direction
                this.piece(ctx, hd.piece, 'grip', [a.W[0] + Math.cos(ha) * 50, a.W[1] + Math.sin(ha) * 50], 0, 1, 1, 1, tint);
            } else this.piece(ctx, hd.piece, 'grip', [a.W[0] + 10, a.W[1] + 30], -0.15 + handRot * 0.3, 1, 1, 1, tint);
            ctx.restore();
        };
        // far arm (+ tablet), behind the body
        // part: 'all', or 'upper' / 'lower' when the arm straddles the torso (far arm holding the tablet)
        const arm = (a, tint, tablet, part = 'all') => {
            this._side = a === this.J.armF ? 'F' : 'N';
            if (part === 'upper') { this.limb(ctx, 'upperArm', a.S, a.E, tint); return; }
            // Claude's puff sleeve ends in a rounded, outlined gather at the elbow: drawn first, her
            // forearm's faded top blends over it (knit on knit) and only the puff's rim past the
            // forearm shows. Drawn last (the boss), that rim lay across the sleeve as a seam.
            const upFirst = !!this.cfg.puffSleeve;
            if (part === 'all' && upFirst) this.limb(ctx, 'upperArm', a.S, a.E, tint);
            const fore = Math.atan2(a.W[1] - a.E[1], a.W[0] - a.E[0]);
            const handRot = fore - this.P.hand.axis + a.hand;
            const isF = a === this.J.armF;
            this._side = isF ? 'F' : 'N';
            const hk = this.cfg.handK || 1;   // her own hand piece's size (Claude's was drawn large)
            const defaultHand = (alpha=1) => {
                if(this.cfg.minimal){const m=isF?-1:1;
                    this.piece(ctx,'hand','a',a.W,fore-(m>0?this.P.hand.axis:PI-this.P.hand.axis)+a.hand,m*hk,hk,alpha,tint);
                }else this.piece(ctx,'hand','a',a.W,handRot,hk,hk,alpha,tint);
            };

            // Hand shapes swapped in as whole pieces, each [piece, weight, mirror]. Every drawing is a
            // right hand with the palm to the viewer, mirrored for her far (left) hand, except
            // palmHand, which is drawn as her left hand palm up. When two shapes overlap (one gesture
            // giving way to the next) they share the hand's opacity.
            const pz = this.pose, th = this.P.tabletHand, shapes = [];
            // the swap is quick, in the middle of the move: two whole hands faded over each other for
            // longer read as a ghost hand (two sets of fingers, both see-through)
            const add = (name, w, m) => { if (this.P[name] && w > 0.02) shapes.push([name, smooth(clamp((w - 0.35) / 0.15, 0, 1)), m]); };
            if (tablet < 0.5) {
                // surprised: both hands pressed to her chest, the backs of the hands to us (the
                // drawing is a left hand, so her near, right hand is the mirrored one)
                // ...and the same drawing, fingers together, turned to point down the forearm, is her hand
                // whenever it hangs free (walking, standing): the parts sheet's own hand is spread like a
                // claw. It gives way to every gesture, the hip, the lap and the keyboard.
                const busy = isF ? Math.max(pz.faceHand, pz.point, pz.open, pz.talkF, pz.palmF, pz.clasp, pz.hipHandF, pz.type)
                                 : Math.max(pz.hipHand, pz.lap, pz.openN, pz.talkN, pz.clasp, pz.type);
                add('chestHand', Math.max(pz.handsUp, 1 - busy), isF ? 1 : -1);
                if (isF) {
                    add('peaceHand', pz.faceHand, -1);      // winking: a peace sign beside her face
                    add('pointHand', pz.point, 1);          // giving orders / a point while talking
                    add('openHand', pz.open, -1);           // waving, cheering: five fingers spread
                    add('talkHand', pz.talkF, -1);          // talking: a relaxed open hand
                    add('palmHand', pz.palmF, 1);           // talking: palm up, presenting
                } else {
                    add('openHand', pz.openN, 1);
                    add('talkHand', pz.talkN, 1);
                }
            }
            if (this.gait.moving && !this.mocap && this.P.walkHand && tablet < 0.05 && this.pose.handsUp < 0.05) {
                // This hanging-hand drawing points DOWN; its handedness is opposite to the
                // upward-pointing chest gesture. The near/right hand uses the original image.
                const p = this.P.walkHand, m = isF ? -1 : 1, k = p.scale;
                this.piece(ctx, 'walkHand', 'wrist', a.W, fore - (m > 0 ? p.axis : PI - p.axis) + a.hand, m * k, k, 1, tint);
            } else if (shapes.length) {
                const tot = shapes.reduce((sum, x) => sum + x[1], 0), norm = tot > 1 ? 1 / tot : 1;
                if (tot < 1) defaultHand(1-tot);
                for (const [name, w, m] of shapes) {
                    const p = this.P[name];
                    this.piece(ctx, name, 'wrist', a.W, fore - (m > 0 ? p.axis : PI - p.axis), m, 1, w * norm, tint);
                }
            } else if (th && tablet > 0.02) {
                // holding the tablet: the open hand gives way to the hand gripping it, wrist under the cuff
                const k = smooth(clamp((tablet - 0.35) / 0.15, 0, 1));
                if (k < 1) defaultHand(1-k);
                this.piece(ctx, 'tabletHand', 'wrist', a.W, fore - th.axis + this.tabletTilt, 1, 1, k, tint);
            } else defaultHand();
            if (isF && tablet > 0.05 && !th && this.cfg.held.layer !== 'over') heldPiece(a, handRot, tablet, tint);
            this.limb(ctx, part === 'lower' && this.P.foreArmSolid ? 'foreArmSolid' : 'foreArm', a.E, a.W, tint);
            if (part === 'all' && !upFirst) this.limb(ctx, 'upperArm', a.S, a.E, tint);
            if (isF && tablet > 0.05 && this.cfg.held.layer === 'over') heldPiece(a, handRot, tablet, tint);
        };
        const leg = (l, tint) => {
            this._side = l === J.legs[1] ? 'F' : 'N';
            this.limb(ctx, 'shin', l.K, l.A, tint);
            this.limb(ctx, 'thigh', l.H, l.K, tint);
            // knee cover at the angle halfway between thigh and shin: the outline bends, never steps
            if (this.P.knee) {
                const at = Math.atan2(l.K[1] - l.H[1], l.K[0] - l.H[0]), as = Math.atan2(l.A[1] - l.K[1], l.A[0] - l.K[0]);
                this.piece(ctx, 'knee', 'k', l.K, (at + as) / 2 - this.P.shin.axis, 1, 1, 1, tint);
            }
            // a heel the leg could not reach is lifted: the toes stay down (pushing off)
            this.piece(ctx, 'foot', 'a', l.A, (l.pitch || 0) + clamp((l.lift || 0) / 110, 0, 0.3), 1, 1, 1, tint);
        };
        // Holding the tablet, her far forearm and the tablet come out in FRONT of her body (as in her
        // design); the far upper arm stays behind the shoulder.
        // Holding the tablet, her far forearm and the tablet come out in FRONT of her body (as in her
        // design); the far upper arm stays behind the shoulder. The hand is held out by her side so
        // the elbow clears her waist and the forearm's faded top never lies over her skin.
        const holding = !!this.P.tabletHand && this.pose.tablet > 0.5;
        // The same goes for any pose that brings the far hand in front of her: hands on her chest
        // (surprised), hands clasped (shy), a hand by her face (wink). Behind her body, the torso or
        // the head hid that hand completely. By her face it is drawn after the head.
        const byFace = this.pose.faceHand > 0.5;
        const inFront = holding || byFace || this.pose.handsUp > 0.5 || this.pose.clasp > 0.5;
        arm(J.armF, far, this.pose.tablet, inFront ? 'upper' : 'all');
        // Seated, her lap piece (skirt and thighs as she sits) takes over from the standing skirt and
        // thighs, cross-fading over the middle of sitting down. Her shins hang from its knees (drawn
        // after it, below), a little wider to meet its fuller seated thighs.
        const lapK = this.P.lap && J.lapLegs ? smooth(clamp((smooth(clamp(this.pose.sit, 0, 1)) - 0.35) / 0.3, 0, 1)) : 0;
        if (lapK < 1) {
            ctx.save(); ctx.globalAlpha *= 1 - lapK;
            leg(J.legs[1], far);
            leg(J.legs[0], null);
            ctx.restore();
        }
        this._side = '';
        this.piece(ctx, 'torso', 'waist', J.waist, J.tRot);
        // soft limit: big swings ease into ±18 instead of stopping dead at a clamp
        const bust = 18 * Math.tanh(this.sp.bust.x / 16);
        let logoAt = J.waist;
        if (Math.abs(bust) > 0.25) {
            this.drawBust(ctx, J.waist, J.tRot, bust);
            // the wordmark sits on the fabric under the chest: carry it by the warp at its centre
            const L = this.P.torsoLogo, T = this.P.torso.j.waist;
            if (L) {
                const lc = [T[0] - L.j.waist[0] + L.j.c[0], T[1] - L.j.waist[1] + L.j.c[1]];
                const d = this.rot(this.bustDisp(lc[0], lc[1], bust), J.tRot);
                logoAt = [J.waist[0] + d[0], J.waist[1] + d[1]];
            }
        }
        this.decal(ctx, 'torsoLogo', 'waist', logoAt, J.tRot);
        const flare = 1 + Math.abs(this.sp.skirt.x) * 0.25;
        if (lapK < 1) this.piece(ctx, 'skirt', 'waist', J.pelvis, J.pRot + this.sp.skirt.x * 0.6, flare, 1 / Math.sqrt(flare), 1 - lapK);
        if (lapK > 0) this.piece(ctx, 'lap', 'waist', J.pelvis, J.pRot, 1, 1, lapK);
        // her shins hang from the lap's knees, drawn over its painted shin stubs: their tops fade in
        // (shinSoft), so the stocking runs on without a seam
        if (lapK > 0) {
            ctx.save(); ctx.globalAlpha *= lapK;
            const sh = this.P.shinSoft ? 'shinSoft' : 'shin';
            // each shin sits over its painted stub: the near stub is fuller at the calf (behind her),
            // the far one reaches further forward, so the shins are widened and nudged to match
            for (const [l, tint, dx] of [[J.lapLegs[1], far, 10], [J.lapLegs[0], null, -8]]) {
                this._side = l === J.lapLegs[1] ? 'F' : 'N';
                const K = [l.K[0] + dx, l.K[1]], A = [l.A[0] + dx, l.A[1]];
                this.limb(ctx, sh, K, A, tint, null, 1.36);
                this.piece(ctx, 'foot', 'a', A, 0, 1, 1, 1, tint);
            }
            ctx.restore();
        }
        if (inFront && !byFace) arm(J.armF, farFront, this.pose.tablet, 'lower');
        this._side = '';
        // Front braids are finished after the near sleeve; otherwise that sleeve hides the tail.
        if (this.hasFaces) {
            this.piece(ctx, 'head_' + this.mood, 'neck', J.neck, J.hRot);
            const eyes = this.eyePatch();
            if (eyes) this.piece(ctx, eyes, 'neck', J.neck, J.hRot);
            if (this.mouthShape) this.piece(ctx, this.mouthShape, 'neck', J.neck, J.hRot);
        } else this.piece(ctx, 'head', 'neck', J.neck, J.hRot);
        const bowAt = [J.tie[0] - 4, J.tie[1] - 2], bowRot = J.hRot - 0.1 + this.sp.bow.x;
        if (this.P.bow) {
            this.piece(ctx, 'bow', 'knot', bowAt, bowRot);
            this.decal(ctx, 'bowLogo', 'knot', bowAt, bowRot);
        }
        if (byFace) arm(J.armF, farFront, this.pose.tablet, 'lower');
        this._side = '';
        // tucked under her near arm (her laptop), drawn before the arm so the arm holds it to her side
        const nh = this.cfg.near;
        if (nh && this.P[nh.piece] && this.pose.hipHand > 0.3) {
            ctx.save(); ctx.globalAlpha *= smooth(clamp((this.pose.hipHand - 0.45) / 0.15, 0, 1));
            this.piece(ctx, nh.piece, 'grip', J.armN.W, nh.rot, 1, 1, 1, null);
            ctx.restore();
        }
        arm(J.armN, null, 0);
        this._side = '';
        if (this.cfg.hair.front) {
            ctx.save();
            // Keep the attachment under the painted head; expose only the hanging braid below it.
            ctx.beginPath(); ctx.rect(-2000, J.neck[1] + 36, 4000, 4000); ctx.clip();
            this.drawTail(ctx, J.hRot);
            ctx.restore();
        }

        if (this.debug) {
            ctx.lineWidth = 6; ctx.strokeStyle = '#3cf'; ctx.fillStyle = '#ff0';
            const line = (a, b) => { ctx.beginPath(); ctx.moveTo(a[0], a[1]); ctx.lineTo(b[0], b[1]); ctx.stroke(); };
            const dot = (a) => { ctx.beginPath(); ctx.arc(a[0], a[1], 10, 0, 2 * PI); ctx.fill(); };
            for (const l of J.legs) { line(l.H, l.K); line(l.K, l.A); dot(l.H); dot(l.K); dot(l.A); }
            for (const a of [J.armN, J.armF]) { line(a.S, a.E); line(a.E, a.W); dot(a.S); dot(a.E); dot(a.W); }
            line(J.waist, J.neck); dot(J.pelvis); dot(J.neck); dot(J.tie);
            ctx.strokeStyle = '#f6a'; for (let i = 1; i < this.tail.n; i++) line(this.tail.p[i - 1], this.tail.p[i]);
        }
        ctx.restore();
    }
}
