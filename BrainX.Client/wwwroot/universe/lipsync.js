// lipsync.js — her mouth, driven from the audio she is playing.
//
// WHY THIS REPLACES THE OLD MOUTH ENTIRELY. The parametric face had no phoneme
// data anywhere near it, so the best available approach was to split the
// spectrum into low/mid/high and bend a mesh by it — an approximation of vowel
// shape with nothing to compare against. A VRM carries the real morphs, five of
// them (aa ih ou ee oh), authored by whoever built the model. The analysis
// hardly changes; where it POINTS changes completely.
//
// WHY BANDS AND NOT AMPLITUDE, STILL. Driving a mouth from loudness gives a
// puppet that flaps with the volume — wrong, because mouth SHAPE is set by
// formants, not level. The split into low/mid/high is what makes "oo" and "ee"
// come out different from nothing but an FFT, with no phoneme table and no
// per-language work, which matters when the language is Thai and viseme data is
// scarce.
//
// WHY TWO SIGNALS FROM TWO PLACES. How far the mouth OPENS comes from the raw
// waveform: an amplitude envelope read straight off the samples has no analysis
// lag at all, where the same envelope derived from FFT magnitudes inherits the
// window and the smoothing. WHICH SHAPE it makes stays on the FFT — vowel
// colour changes far slower than loudness, so it can afford latency the opening
// cannot. Getting this backwards is what makes a mouth look a beat behind.

const LOW = [0, 8], MID = [8, 40], HIGH = [40, 120];

/**
 * Where each viseme sits on two axes: how open the mouth is, and how spread
 * (1) versus rounded (0) it is. Blending by distance on this plane means the
 * mouth moves BETWEEN shapes instead of snapping between five poses, which is
 * the difference between speech and a slideshow.
 */
const SHAPES = [
    { id: 'aa', open: 1.00, spread: 0.50 },
    { id: 'ih', open: 0.45, spread: 0.90 },
    { id: 'ee', open: 0.35, spread: 1.00 },
    { id: 'oh', open: 0.70, spread: 0.15 },
    { id: 'ou', open: 0.35, spread: 0.00 },
];

function band(data, [a, b]) {
    let s = 0;
    for (let i = a; i < b && i < data.length; i++) s += data[i];
    return s / Math.max(1, Math.min(b, data.length) - a) / 255;
}

export class LipSync {
    constructor() {
        this.speaking = false;
        this.level = 0;              // 0..1 loudness, for anything else that wants it
        this._open = 0;
        this._spread = 0;
        this.weights = { aa: 0, ih: 0, ou: 0, ee: 0, oh: 0 };
    }

    /**
     * Play an mp3 and drive the mouth from it. Resolves when it finishes.
     *
     * The AudioContext is created HERE, not in the constructor: browsers refuse
     * to start one outside a user gesture, and one created at load time arrives
     * suspended and silently stays that way — audible as "her mouth never
     * moves", with no error anywhere to explain it.
     */
    async play(url) {
        this.stop();
        const audio = new Audio(url);
        audio.crossOrigin = 'anonymous';
        this.audio = audio;

        const Ctx = window.AudioContext || window.webkitAudioContext;
        this.ctx = this.ctx || new Ctx();
        if (this.ctx.state === 'suspended') { try { await this.ctx.resume(); } catch {} }

        const an = this.ctx.createAnalyser();
        // SMALL WINDOW, ALMOST NO SMOOTHING. smoothingTimeConstant is an
        // exponential average over frames: at 0.55 each frame is 55% history,
        // two or three frames of lag on top of the FFT window — enough to blur
        // consecutive syllables into one long vowel. Thai is syllable-timed, so
        // that blur is precisely what makes the mouth look out of step.
        an.fftSize = 256;
        an.smoothingTimeConstant = 0.1;
        this.ctx.createMediaElementSource(audio).connect(an);
        an.connect(this.ctx.destination);
        this.analyser = an;
        this.freq = new Uint8Array(an.frequencyBinCount);
        this.time = new Uint8Array(an.fftSize);

        this.speaking = true;
        try { await audio.play(); } catch (e) { this.speaking = false; throw e; }
        await new Promise(res => { audio.onended = res; audio.onerror = res; });
        this.speaking = false;
        return true;
    }

    stop() {
        try { this.audio?.pause(); } catch {}
        this.speaking = false;
    }

    /** Recompute the viseme weights. Call once a frame. */
    update() {
        let open = 0, spread = 0;

        if (this.speaking && this.analyser) {
            this.analyser.getByteTimeDomainData(this.time);
            let sum = 0;
            for (let i = 0; i < this.time.length; i++) {
                const v = (this.time[i] - 128) / 128;
                sum += v * v;
            }
            const rms = Math.sqrt(sum / this.time.length);
            // Gated by a noise floor, or room tone and codec hiss hold her
            // mouth permanently ajar between sentences.
            open = Math.min(1, Math.max(0, (rms - 0.012) * 6.2));
            this.level = Math.min(1, rms * 3.6);

            this.analyser.getByteFrequencyData(this.freq);
            const lo = band(this.freq, LOW), mid = band(this.freq, MID), hi = band(this.freq, HIGH);
            spread = Math.min(1, Math.max(0, hi * 2.4 + mid * 0.7 - lo * 0.6));
        } else {
            this.level += (0 - this.level) * 0.1;
        }

        // Asymmetric: mouths open faster than they close, and equal rates read
        // as mush. Attack near-instant, release merely quick — one that closes
        // as fast as it opens chatters between syllables.
        this._open += (open - this._open) * (open > this._open ? 0.90 : 0.38);
        this._spread += (spread - this._spread) * 0.34;

        return this.shape(this._open, this._spread);
    }

    /**
     * Nearest-shapes blend on the (open, spread) plane, normalised so the
     * weights always sum to the current OPENING rather than to whatever the
     * distances happened to produce. Split out from `update` so the mapping can
     * be checked without an audio file, which is the only part of this worth
     * testing and the only part that has no browser dependency.
     */
    shape(open, spread) {
        let total = 0;
        const raw = SHAPES.map(s => {
            const d = Math.hypot((open - s.open) * 0.9, (spread - s.spread) * 1.1);
            const w = 1 / (0.08 + d * d * 6);
            total += w;
            return w;
        });
        for (let i = 0; i < SHAPES.length; i++)
            this.weights[SHAPES[i].id] = (raw[i] / total) * open;
        return this.weights;
    }

    /** Push the current weights onto a VRM's expression manager. */
    applyTo(vrm) {
        const em = vrm?.expressionManager;
        if (!em) return;
        for (const k in this.weights) em.setValue(k, this.weights[k]);
    }

    dispose() {
        this.stop();
        try { this.ctx?.close(); } catch {}
    }
}
