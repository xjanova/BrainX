// Interior art remains fixed; only the view behind its extracted window alpha moves.
const W = 1536, H = 1024;
const fract = x => x - Math.floor(x);
const hash = n => fract(Math.sin(n * 127.1 + 31.7) * 43758.5453);
const stars = Array.from({length: 420}, (_, i) => ({
  x: hash(i + 1), y: hash(i + 301), depth: hash(i + 701), phase: hash(i + 1101),
}));
// Reuse one soft streak texture instead of allocating hundreds of gradients per frame.
const streak = document.createElement('canvas'); streak.width = 256; streak.height = 12;
const ink = streak.getContext('2d'), streakGlow = ink.createLinearGradient(0, 0, 256, 0);
streakGlow.addColorStop(0, 'rgba(120,100,245,0)');
streakGlow.addColorStop(.65, 'rgba(125,155,255,.6)');
streakGlow.addColorStop(1, 'rgba(205,237,255,1)');
ink.strokeStyle = streakGlow; ink.lineCap = 'round';
ink.globalAlpha = .2; ink.lineWidth = 10; ink.beginPath(); ink.moveTo(0, 6); ink.lineTo(251, 6); ink.stroke();
ink.globalAlpha = 1; ink.lineWidth = 3; ink.stroke();

export function makeWindowPlate(original, extracted) {
  const plate = document.createElement('canvas'); plate.width = W; plate.height = H;
  const p = plate.getContext('2d');
  p.drawImage(extracted, 0, 0, W, H);
  const mask = p.getImageData(0, 0, W, H);
  // Generation may give opaque paint alpha 253/254. Interior surfaces and module seams
  // must be fully opaque; retain soft alpha only in the window area, above the furniture.
  for (let y = 0; y < H; y++) for (let x = 0; x < W; x++) {
    const k = (y * W + x) * 4 + 3;
    if (x < 100 || x > 1430 || y < 80 || y > 430 || mask.data[k] >= 245) mask.data[k] = 255;
  }
  p.putImageData(mask, 0, 0);
  p.globalCompositeOperation = 'source-in';
  // Re-use original paint, preserving furniture/chair alignment and exact adjoining seams.
  p.drawImage(original, 0, 0, W, H); p.globalCompositeOperation = 'source-over';
  return plate;
}

/** `pixels` is screen pixels per world unit. Far out the window is a sliver a hundred pixels
 *  wide and 420 streaks in it are a smear nobody can count; draw as many as the size can show. */
export function drawWindowView(ctx, roomX, time, forward = false, pixels = 1) {
  const every = pixels >= 0.6 ? 1 : pixels >= 0.3 ? 2 : pixels >= 0.15 ? 4 : 8;
  ctx.save(); ctx.translate(roomX, 0);
  ctx.beginPath(); ctx.rect(100, 80, 1330, 355); ctx.clip();
  const bg = ctx.createLinearGradient(0, 80, 0, 435);
  bg.addColorStop(0, '#060c25'); bg.addColorStop(.55, '#121c48'); bg.addColorStop(1, '#100c2c');
  ctx.fillStyle = bg; ctx.fillRect(100, 80, 1330, 355);
  // Slow distant colour behind fast stars; the field has no hard wrap at a window frame.
  const centerX = forward ? 785 : 760 + Math.sin(time * .065 + roomX * .0001) * 430;
  const glow = ctx.createRadialGradient(centerX, 225, 12, centerX, 225, forward ? 610 : 850);
  glow.addColorStop(0, forward ? 'rgba(100,165,255,.7)' : 'rgba(85,47,150,.5)');
  glow.addColorStop(.4, 'rgba(39,43,115,.3)'); glow.addColorStop(1, 'rgba(13,16,45,0)');
  ctx.fillStyle = glow; ctx.fillRect(100, 80, 1330, 355);
  ctx.globalCompositeOperation = 'screen'; ctx.lineCap = 'round';
  for (let si = 0; si < stars.length; si += every) {
    const s = stars[si];
    let x, y, tx, ty, alpha;
    if (forward) {
      // Perspective flight: points accelerate radially as they approach the cockpit.
      const age = fract(time * (.18 + s.depth * .12) + s.phase);
      const radius = .035 + age * age * 1.85;
      const angle = s.x * Math.PI * 2, dx = Math.cos(angle) * 900, dy = Math.sin(angle) * 500;
      const tailRadius = Math.max(.025, radius - (.035 + age * .24));
      x = 785 + dx * radius; y = 225 + dy * radius;
      tx = 785 + dx * tailRadius; ty = 225 + dy * tailRadius;
      alpha = Math.min(1, age * 3) * Math.min(1, (1 - age) * 8) * (.45 + s.depth * .5);
    } else {
      // One world-space side field shared by all rooms: the ship travels to the right.
      const span = 4096, speed = 650 + s.depth * 1400;
      x = fract(s.x - (time * speed + roomX) / span) * span - 100;
      y = 80 + s.y * 355;
      tx = x + 45 + s.depth * s.depth * 230; ty = y - 3 - s.depth * 6;
      alpha = .35 + s.depth * .55;
    }
    if (Math.max(x, tx) < 100 || Math.min(x, tx) > 1430 || Math.max(y, ty) < 80 || Math.min(y, ty) > 435) continue;
    const length = Math.hypot(x - tx, y - ty), width = 1 + s.depth * 2.2;
    ctx.save(); ctx.translate(tx, ty); ctx.rotate(Math.atan2(y - ty, x - tx));
    ctx.globalAlpha = alpha; ctx.drawImage(streak, 0, -width * 2, length, width * 4); ctx.restore();
  }
  ctx.globalCompositeOperation = 'source-over';
  // A low-contrast reflection gives the opening a glass surface without obscuring travel.
  const sheen = ctx.createLinearGradient(150, 80, 1100, 435);
  sheen.addColorStop(0, 'rgba(140,210,255,.045)'); sheen.addColorStop(.5, 'rgba(90,150,220,0)');
  sheen.addColorStop(1, 'rgba(120,160,240,.025)');
  ctx.fillStyle = sheen; ctx.fillRect(100, 80, 1330, 355);
  ctx.restore();
}
