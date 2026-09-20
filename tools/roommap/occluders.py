# Derive the occluder layer from the floor the OWNER painted.
#
# Owner (2026-09-20): "ไอ้ที่ต้องบังมันต้องไต่ตามขอบของไง เอไอต้องทำให้จะได้เร็ว
# และบางชิ้นเป็นหน้าต่าง บางอันเป็นพื้น ไม่ใช่หลังตู้ แต่ตัวละครไปอยู่หลังพื้น
# มันผิด".
#
# Two things had to change, and only one of them was the rule.
#
# THE RULE. "Has floor above it" is necessary and not sufficient — a back
# wall, a window and a patch of missed floor all have floor above them, and a
# character can stand in front of none of them. What a real occluder has is
# floor on BOTH sides in its own column: floor above (somewhere to stand
# behind it) and floor below (the ground it stands on, nearer the camera). A
# sofa passes. A wall has no floor below it, only itself. A window has neither.
# A patch of floor is floor, and is thrown out by colour.
#
# THE INPUT. My own floor detection missed the patterned rug in front of the
# sofa — busy print, high local variance — and without that rug the sofa has
# no ground under it and the rule threw the sofa away too. The owner's painted
# mask has that rug, and their walkable layer survived the palette bug intact.
# So the floor comes from their painting, and the occluders are computed from
# it. They paint what they can see; I compute what follows from it.
#
# The result is per-PIXEL, so the outline is the object's own outline — which
# is the "ไต่ตามขอบ" half of the ask. No boxes anywhere.
import io, json, os, sys
import numpy as np
from PIL import Image, ImageDraw

import argparse

ap = argparse.ArgumentParser(
    description="Derive the occluder layer from a painted floor. Reads room.webp + "
                "room-mask.png from a folder and rewrites that mask's blue channel.")
ap.add_argument("art", nargs="?",
                default=os.path.join("BrainX.Client", "wwwroot", "office", "art"),
                help="folder holding room.webp and room-mask.png")
ap.add_argument("-o", "--out", default=None,
                help="where to write the review image (default: the art folder)")
_args = ap.parse_args()
ART = _args.art
OUT = _args.out or ART

plate = Image.open(os.path.join(ART, "room.webp")).convert("RGB")
W, H = plate.size
a = np.asarray(plate, dtype=np.int32)

m = np.asarray(Image.open(os.path.join(ART, "room-mask.png")).convert("RGB"))
blocked = m[..., 0] > 127
floor = (m[..., 1] > 127) & ~blocked

# ── the floor's own colours, so furniture can be told from ground ────
fh = np.zeros(32 * 32 * 32, np.int64)
fpx = a[floor] >> 3
np.add.at(fh, (fpx[:, 0] << 10) | (fpx[:, 1] << 5) | fpx[:, 2], 1)
peak = max(1, fh.max())
idx = (a >> 3)
idx = (idx[..., 0] << 10) | (idx[..., 1] << 5) | idx[..., 2]
is_floor_colour = fh[idx] >= peak * 0.25

# ── floor above / floor below, per column ────────────────────────────
above = np.cumsum(floor, axis=0) > 0
below = np.cumsum(floor[::-1], axis=0)[::-1] > 0

# How far under a pixel the ground is allowed to be. Anything further and it
# is a wall with a floor somewhere at the bottom of the frame, not a thing
# standing on that floor.
REACH = int(H * 0.22)
near_below = np.zeros_like(floor)
acc = np.zeros(W, np.int32)
for y in range(H - 1, -1, -1):
    acc = np.where(floor[y], 0, acc + 1)
    near_below[y] = (acc <= REACH) & below[y]

# ── the occluder layer ───────────────────────────────────────────────
#
# Not floor, has floor behind it, stands on floor in front of it, and does not
# look like the floor. Everything else in the picture — walls, windows, the
# city, the ceiling — fails at least one of those.
occl = (~floor) & (~is_floor_colour) & above & near_below

# Clean up: close pinholes inside furniture, drop dust. Box means rather than
# a morphology library, which is not installed here and would be overkill.
def box(bits, r):
    p = np.pad(bits.astype(np.float32), r, mode="constant")
    ii = p.cumsum(0).cumsum(1)
    ys = np.arange(H)[:, None]; xs = np.arange(W)[None, :]
    s = (ii[ys + 2 * r, xs + 2 * r] - ii[ys, xs + 2 * r]
         - ii[ys + 2 * r, xs] + ii[ys, xs])
    return s / ((2 * r) ** 2)

occl = box(occl, 3) > 0.45     # close
occl = box(occl, 3) > 0.55     # open
occl &= ~floor

out = np.zeros((H, W, 3), np.uint8)
out[..., 0] = blocked * 255
out[..., 1] = floor * 255
out[..., 2] = occl * 255
Image.fromarray(out).save(os.path.join(ART, "room-mask.png"))

tot = W * H
print("from your painted floor:")
print("  walkable %5.1f%%   blocked %5.1f%%   occluder %5.1f%%"
      % (floor.sum() / tot * 100, blocked.sum() / tot * 100, occl.sum() / tot * 100))

# ── show it ──────────────────────────────────────────────────────────
view = plate.convert("RGBA")
for layer, colour in ((floor, (60, 255, 140, 70)), (blocked, (255, 70, 90, 55)), (occl, (90, 160, 255, 150))):
    tint = Image.new("RGBA", (W, H), colour)
    view = Image.alpha_composite(view, Image.composite(
        tint, Image.new("RGBA", (W, H), (0, 0, 0, 0)),
        Image.fromarray((layer * 255).astype(np.uint8))))
view.convert("RGB").save(os.path.join(OUT, "occl-review.png"))
print("wrote occl-review.png (blue = will be drawn over a character standing behind it)")
