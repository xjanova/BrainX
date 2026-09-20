# Automatic room segmentation, take two — seeded region growing.
#
# Take one clustered by colour and failed for a reason worth keeping: in this
# art the walls, the floor and half the furniture share a palette. Colour alone
# cannot separate them, and no amount of scoring rescues a cluster that is
# genuinely both.
#
# What DOES separate them is the two things a picture of a room carries that a
# palette does not:
#
#   FLATNESS  the floor is the large low-detail area; furniture, plants and
#             shelves are busy. A local-variance map finds that immediately.
#   CONTINUITY the floor is one connected surface under the camera. Growing it
#             from seeds INSIDE it, and stopping at edges, keeps the sofa out
#             even where the sofa is the same brightness as the boards.
#
# So: flatness picks the seeds, region growing bounded by an edge map does the
# rest, and depth (what occludes what) still comes from the projection — lower
# on screen is nearer the viewer.
import argparse, io, json, os
import numpy as np
from PIL import Image, ImageDraw

# Owner (2026-09-20): "ตัวนี้ต้องเก็บไว้ใช้เลย พวกเกมเราก็ต้องใช้ในอนาคต".
# It takes a path rather than one hardcoded room, so it works on the next
# plate and the next game:
#
#   python tools/roommap/autoseg.py <plate.png> [-o OUTDIR]
#
# It is a FIRST PASS, not an answer. Open the result in
# wwwroot/office/tools/mask-paint.html and correct it — faster than painting
# 1.5M pixels from nothing, and only slower than nothing if it were wrong
# everywhere, which the preview it writes lets you check in one look.



def box_mean(a, r):
    """Mean over a (2r+1) box, via a summed-area table. Used for both the
    variance map and the morphology below, so it is worth having exactly."""
    H, W = a.shape
    pad = np.pad(a, r + 1, mode="edge")
    ii = pad.cumsum(0).cumsum(1)
    y0, y1 = np.mgrid[0:H, 0:W][0], None
    ys = np.arange(H)[:, None]
    xs = np.arange(W)[None, :]
    a1 = ii[ys, xs]
    a2 = ii[ys, xs + 2 * r + 1]
    a3 = ii[ys + 2 * r + 1, xs]
    a4 = ii[ys + 2 * r + 1, xs + 2 * r + 1]
    area = (2 * r + 1) ** 2
    return (a4 - a2 - a3 + a1) / area


def grow(seedmask, allow, iters=400):
    """Dilate `seedmask` inside `allow` until it stops changing. Plain binary
    dilation by a 3x3 cross, vectorised — a flood fill written as array shifts,
    which is fast enough at half resolution and has no recursion to blow."""
    cur = seedmask & allow
    for _ in range(iters):
        nxt = cur.copy()
        nxt[1:, :] |= cur[:-1, :]
        nxt[:-1, :] |= cur[1:, :]
        nxt[:, 1:] |= cur[:, :-1]
        nxt[:, :-1] |= cur[:, 1:]
        nxt &= allow
        if nxt.sum() == cur.sum():
            break
        cur = nxt
    return cur


def components(mask):
    H, W = mask.shape
    lab = np.zeros((H, W), np.int32)
    cur = 0
    for sy in range(H):
        row = mask[sy]
        for sx in range(W):
            if not row[sx] or lab[sy, sx]:
                continue
            cur += 1
            stack = [(sy, sx)]
            lab[sy, sx] = cur
            while stack:
                y, x = stack.pop()
                for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    ny, nx = y + dy, x + dx
                    if 0 <= ny < H and 0 <= nx < W and mask[ny, nx] and not lab[ny, nx]:
                        lab[ny, nx] = cur
                        stack.append((ny, nx))
    return lab, cur


def main():
    ap = argparse.ArgumentParser(
        description="Segment an isometric room plate into walkable / blocked / occluder.")
    ap.add_argument("plate", nargs="?",
                    default=os.path.join("BrainX.Client", "wwwroot", "office", "art", "room.webp"))
    ap.add_argument("-o", "--out", default=None, help="output folder (default: beside the plate)")
    args = ap.parse_args()
    SRC = args.plate
    OUT = args.out or os.path.dirname(os.path.abspath(SRC))
    os.makedirs(OUT, exist_ok=True)

    im = Image.open(SRC).convert("RGB")
    W, H = im.size
    sw, sh = W // 2, H // 2
    small = im.resize((sw, sh), Image.BILINEAR)
    a = np.asarray(small, dtype=np.float32)
    grey = a.mean(2)

    # ── edges: where a region is allowed to stop ─────────────────────
    # Sobel on the greyscale, plus colour change, because two surfaces here can
    # differ in hue at the same brightness (the green sofa against wood).
    gx = np.zeros_like(grey); gy = np.zeros_like(grey)
    gx[:, 1:-1] = grey[:, 2:] - grey[:, :-2]
    gy[1:-1, :] = grey[2:, :] - grey[:-2, :]
    edge = np.hypot(gx, gy)
    ca = np.zeros_like(grey)
    for c in range(3):
        cx = np.zeros_like(grey); cy = np.zeros_like(grey)
        cx[:, 1:-1] = a[:, 2:, c] - a[:, :-2, c]
        cy[1:-1, :] = a[2:, :, c] - a[:-2, :, c]
        ca = np.maximum(ca, np.hypot(cx, cy))
    edge = np.maximum(edge, ca)

    # ── flatness: where the floor is ─────────────────────────────────
    m1 = box_mean(grey, 3)
    m2 = box_mean(grey * grey, 3)
    var = np.maximum(m2 - m1 * m1, 0)
    flat = var < np.percentile(var, 55)

    # Seeds: flat, mid-toned, inside the middle of the frame. The sky is dark
    # and at the top; the balcony wall is dark and at the very bottom.
    ys, xs = np.mgrid[0:sh, 0:sw]
    band = (ys > sh * 0.20) & (ys < sh * 0.93) & (xs > sw * 0.06) & (xs < sw * 0.94)
    v = grey / 255.0
    lower = ys > sh * 0.70
    flat_enough = flat | (lower & (var < np.percentile(var, 78)))
    seeds = flat_enough & band & (v > 0.20) & (v < 0.80) & (edge < 18)

    # Only keep seed blobs that are genuinely large — a flat patch on a
    # cupboard door is flat too, and it is not floor.
    lab, n = components(seeds)
    keep = np.zeros_like(seeds)
    sizes = np.bincount(lab.ravel())
    for i in range(1, n + 1):
        if sizes[i] < sw * sh * 0.004:
            continue
        m = lab == i
        my = ys[m].mean() / sh
        if my < 0.25 or my > 0.90:
            continue
        keep |= m

    # ── grow the floor ───────────────────────────────────────────────
    allow = (edge < 26) & band & (v > 0.16) & (v < 0.86)
    floor = grow(keep, allow)

    # Close the hairline gaps the edge map leaves along board seams.
    f = box_mean(floor.astype(np.float32), 2) > 0.45
    floor = f & band

    # ── the room, and what stands in it ──────────────────────────────
    # The room extends BELOW the floor as well as above it. Everything in the
    # front row — the sofa, the coffee bar, the planters, the balcony rail —
    # stands nearer the camera than the boards behind it, so its pixels sit
    # under the floor's lowest pixel in that column. Growing only upward (the
    # first version) left exactly those pieces unclassified, which is also why
    # the occluder layer came out nearly empty: the things that occlude are
    # precisely the things in front.
    room = np.zeros_like(floor)
    for x in range(sw):
        col = np.where(floor[:, x])[0]
        if len(col):
            top = max(0, col.min() - int(sh * 0.18))
            bot = min(sh, col.max() + int(sh * 0.16))
            room[top:bot, x] = True
    # The night sky and the city are not furniture. They are dark and sit
    # outside the lit interior, so brightness rejects them cleanly.
    for y in range(sh):
        row = np.where(floor[y])[0]
        if len(row):
            room[y, max(0, row.min() - int(sw * 0.16)):min(sw, row.max() + int(sw * 0.16))] = True
    room &= (v > 0.14)
    objects = room & ~floor

    # ── occluders: floor above, in the same column ───────────────────
    floor_above = np.zeros_like(floor)
    acc = np.zeros(sw, bool)
    for y in range(sh):
        acc |= floor[y]
        floor_above[y] = acc

    lab2, n2 = components(objects)
    occl = np.zeros_like(objects)
    for i in range(1, n2 + 1):
        m = lab2 == i
        sz = int(m.sum())
        if sz < 60:
            continue
        if float((m & floor_above).sum()) / sz > 0.5:
            occl |= m

    # ── seats: the floor strip in front of a big object ──────────────
    seats = []
    for i in range(1, n2 + 1):
        m = lab2 == i
        if m.sum() < sw * sh * 0.006:
            continue
        cols = np.where(m.any(0))[0]
        if len(cols) < sw * 0.07:
            continue
        cx = int(np.median(cols))
        rows = np.where(m[:, cx])[0]
        if not len(rows):
            continue
        fy = rows.max() + 5
        if fy < sh and floor[min(sh - 1, fy), cx]:
            seats.append({"x": round(cx / sw, 4), "y": round(fy / sh, 4)})
    seats = seats[:8]

    def up(mask):
        return np.asarray(Image.fromarray((mask * 255).astype(np.uint8))
                          .resize((W, H), Image.NEAREST)) > 127

    Fw, Bl, Oc = up(floor), up(objects), up(occl)
    rgb = np.zeros((H, W, 3), np.uint8)
    rgb[..., 0] = Bl * 255
    rgb[..., 1] = Fw * 255
    rgb[..., 2] = Oc * 255
    Image.fromarray(rgb).save(os.path.join(OUT, "room-mask.png"))
    io.open(os.path.join(OUT, "room-seats.json"), "w", encoding="utf-8").write(json.dumps(
        {"note": "auto-segmented (flatness + region growing); seats are guesses", "seats": seats},
        ensure_ascii=False, indent=2))

    view = im.convert("RGBA")
    for m, colour in ((Fw, (60, 255, 140, 90)), (Bl, (255, 70, 90, 80)), (Oc, (90, 160, 255, 120))):
        tint = Image.new("RGBA", (W, H), colour)
        view = Image.alpha_composite(view, Image.composite(
            tint, Image.new("RGBA", (W, H), (0, 0, 0, 0)),
            Image.fromarray((m * 255).astype(np.uint8))))
    d = ImageDraw.Draw(view)
    for i, s in enumerate(seats):
        x, y = s["x"] * W, s["y"] * H
        d.ellipse([x - 9, y - 9, x + 9, y + 9], fill=(255, 209, 102, 255))
        d.text((x - 3, y - 6), str(i + 1), fill=(42, 32, 8, 255))
    view.convert("RGB").save(os.path.join(OUT, "room-mask-preview.png"))

    tot = W * H
    print("%s  %dx%d" % (os.path.basename(SRC), W, H))
    print("  walkable %5.1f%%  blocked %5.1f%%  occluder %5.1f%%  seats %d"
          % (Fw.sum() / tot * 100, Bl.sum() / tot * 100, Oc.sum() / tot * 100, len(seats)))
    print("  -> room-mask.png, room-seats.json, room-mask-preview.png in " + OUT)


if __name__ == "__main__":
    main()
