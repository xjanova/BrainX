// panorama.js — turn a flat 2:1 picture into something that can be wrapped
// round a sphere without giving itself away.
//
// Two faults show up the moment a generated panorama is used as a sky, and
// neither is the image's fault — no model is told the picture has to close on
// itself, and none of them know what equirect does to the top row.
//
// THE SEAM. An equirect texture wraps at x=0/x=W, so the last column has to
// continue into the first. A generated one does not, and the join is a hard
// vertical line that a drifting sky walks through the frame every few minutes.
// The repair is one composite: the left band, mirrored, laid over the right
// band behind an alpha ramp that reaches full strength exactly at the last
// column. Mirroring is what makes it exact rather than close — the far end of
// that band IS source column 0, which is also canvas column 0, so the two
// edges meet on the same pixels whatever the image happens to contain.
// Measured on the sky both windows use: mean per-channel difference between
// the first and last column went 10.03 -> 0.13, against 30.3 for two columns
// picked at random.
//
// THE POLES. Equirect pinches the top and bottom rows to a single point, so
// whatever is left in them becomes a pinwheel at the zenith as soon as the
// camera is dragged up. Fading them to a flat colour leaves nothing to spin.
// The same fade doubles as the END of the sky for a texture sampled with
// ClampToEdge, because then that last row is what the whole sky beyond the
// band becomes — which is why the colour is a parameter and not a constant.
// Black for a dome drawn with additive blending, where black adds nothing;
// the page's own background colour for one drawn normally, where it has to
// match what is behind it.

/**
 * @param {HTMLImageElement|HTMLCanvasElement} img  a 2:1 equirectangular image
 * @param {{voidColor?: number[], top?: number, bottom?: number, band?: number,
 *          filter?: string}} opts
 *        voidColor  [r,g,b] 0-255 that the top and bottom fade into
 *        top/bottom fraction of the height each fade covers
 *        band       fraction of the width used to cross-fade the seam
 *        filter     CSS filter applied to the finished image; see below
 * @returns {HTMLCanvasElement} ready to hand to THREE.CanvasTexture
 */
export function seamlessEquirect(img, opts = {}) {
    const { voidColor = [0, 0, 0], top = 0.12, bottom = 0.12, band = 0.07,
            filter = null } = opts;
    const W = img.naturalWidth || img.width;
    const H = img.naturalHeight || img.height;

    const c = document.createElement('canvas');
    c.width = W; c.height = H;
    const ctx = c.getContext('2d');
    ctx.drawImage(img, 0, 0);

    const B = Math.max(8, Math.round(W * band));
    const strip = document.createElement('canvas');
    strip.width = B; strip.height = H;
    const sx = strip.getContext('2d');
    sx.translate(B, 0); sx.scale(-1, 1);
    sx.drawImage(img, 0, 0, B, H, 0, 0, B, H);
    sx.setTransform(1, 0, 0, 1, 0, 0);
    sx.globalCompositeOperation = 'destination-in';
    sx.fillStyle = ramp(sx.createLinearGradient(0, 0, B, 0), voidColor, 0, 1);
    sx.fillRect(0, 0, B, H);
    ctx.drawImage(strip, W - B, 0);

    const t = Math.round(H * top), b = Math.round(H * bottom);
    if (t > 0) {
        ctx.fillStyle = ramp(ctx.createLinearGradient(0, 0, 0, t), voidColor, 1, 0);
        ctx.fillRect(0, 0, W, t);
    }
    if (b > 0) {
        ctx.fillStyle = ramp(ctx.createLinearGradient(0, H - b, 0, H), voidColor, 0, 1);
        ctx.fillRect(0, H - b, W, b);
    }
    return filter ? graded(c, filter) : c;
}

/**
 * Re-draw the finished panorama through a CSS filter.
 *
 * This exists for the dome the dashboard draws with ADDITIVE blending, where
 * the photograph's "empty" sky is not empty — it is a dark navy that adds a
 * constant to every pixel of the view, and what should read as a galaxy over
 * black arrives as a flat haze with a band somewhere in it. Pushing contrast
 * takes the voids to true black and leaves the band standing, which is both
 * prettier and quieter: less total light on screen, more of it in the shape
 * that means something.
 *
 * Applied as a whole-image pass rather than during the draws above, so it
 * cannot break the seam — the repair made column 0 and column W-1 equal, and
 * a per-pixel curve maps equal inputs to equal outputs.
 */
function graded(src, filter) {
    const out = document.createElement('canvas');
    out.width = src.width; out.height = src.height;
    const ctx = out.getContext('2d');
    ctx.filter = filter;
    ctx.drawImage(src, 0, 0);
    return out;
}

/**
 * Load a panorama and prepare it, or null. A sky is scenery: a missing file is
 * not worth stopping a window for, and every caller has something to fall back
 * to that is better than a stack trace.
 *
 * @param {string} url
 * @param {object} opts  passed through to seamlessEquirect
 * @returns {Promise<HTMLCanvasElement|null>}
 */
export async function loadPanorama(url, opts = {}) {
    try {
        const img = await new Promise((res, rej) => {
            const i = new Image();
            i.onload = () => res(i);
            i.onerror = () => rej(new Error('panorama: ' + url));
            i.src = url;
        });
        return seamlessEquirect(img, opts);
    } catch {
        return null;
    }
}

/** A gradient of one colour running from alpha `a0` to `a1`. */
function ramp(g, [r, gr, b], a0, a1) {
    g.addColorStop(0, `rgba(${r},${gr},${b},${a0})`);
    g.addColorStop(1, `rgba(${r},${gr},${b},${a1})`);
    return g;
}
