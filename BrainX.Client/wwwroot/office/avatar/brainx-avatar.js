/* BrainX Canvas Avatar — dependency-free ES module. */
export class BrainXAvatar {
  static async load(baseURL = './', options = {}) {
    const base = new URL(baseURL, document.baseURI);
    const response = await fetch(new URL('animations.json', base));
    if (!response.ok) throw new Error('Cannot load animations.json: ' + response.status);
    const data = await response.json();
    const images = await Promise.all(data.pages.map(p => new Promise((resolve, reject) => {
      const image = new Image(); image.onload = () => resolve(image);
      image.onerror = () => reject(new Error('Cannot load atlas: ' + p));
      image.src = new URL(p, base).href;
    })));
    return new BrainXAvatar(data, images, options);
  }
  constructor(data, images, options = {}) {
    this.data = data; this.images = images;
    this.x = options.x ?? 0; this.y = options.y ?? 0;
    this.scale = options.scale ?? 1; this.speed = options.speed ?? 80;
    this.direction = 'se'; this.posture = 'standing';
    this.time = 0; this.rate = 1; this.paused = false;
    this.queue = []; this.target = null;
    this.onComplete = options.onComplete ?? (() => {});
    this.onEvent = options.onEvent ?? (() => {});
    this.play('idle');
  }
  get animations() { return Object.keys(this.data.animations); }
  get clip() { return this.data.animations[this.name]; }
  get frame() {
    const c = this.clip;
    let t = Math.min(this.time, c.duration - 0.001);
    for (const f of c.frames) { if (t < f.duration) return this.data.frames[f.id]; t -= f.duration; }
    return this.data.frames[c.frames.at(-1).id];
  }
  play(name, { loop, restart = true, clearQueue = true } = {}) {
    const clip = this.data.animations[name];
    if (!clip) throw new Error('Unknown BrainX animation: ' + name);
    if (!restart && this.name === name) return this;
    if (clearQueue) this.queue = [];
    this.name = name; this.time = 0; this.loop = loop ?? clip.loop;
    if (clip.direction) this.direction = clip.direction;
    this.posture = clip.posture || 'standing';
    if (!name.startsWith('walk_') && name !== 'run') this.target = null;
    return this;
  }
  enqueue(name, options = {}) {
    if (!this.data.animations[name]) throw new Error('Unknown BrainX animation: ' + name);
    this.queue.push({ name, options }); return this;
  }
  stop() {
    this.target = null; this.queue = [];
    return this.play(this.posture === 'seated' ? 'seated_idle' : this.idleForDirection());
  }
  idleForDirection() { return this.direction === 'se' ? 'idle' : 'idle_' + this.direction; }
  moveTo(x, y, { speed = this.speed } = {}) {
    if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(speed) || speed <= 0)
      throw new Error('moveTo requires finite coordinates and positive speed.');
    const dx = x - this.x, dy = y - this.y;
    if (Math.hypot(dx, dy) < 0.1) return this.stop();
    const dir = (dy < 0 ? 'n' : 's') + (dx < 0 ? 'w' : 'e');
    this.play('walk_' + dir, { restart: this.name !== 'walk_' + dir });
    this.target = { x, y }; this.speed = speed; return this;
  }
  update(dt) {
    if (!Number.isFinite(dt) || dt <= 0 || this.paused) return;
    // Seconds in, milliseconds in manifest. Clamp tab-resume jumps.
    dt = Math.min(dt, 0.1);
    if (this.target) {
      const dx = this.target.x - this.x, dy = this.target.y - this.y;
      const distance = Math.hypot(dx, dy), step = this.speed * dt;
      if (distance <= step) {
        this.x = this.target.x; this.y = this.target.y;
        this.target = null; this.play(this.idleForDirection()); return;
      }
      this.x += dx / distance * step; this.y += dy / distance * step;
    }
    const gaitRate = this.target && this.clip.nominalSpeed ? this.speed / (this.clip.nominalSpeed * this.scale) : 1;
    let remaining = dt * 1000 * Math.max(0, this.rate) * gaitRate;
    let safety = 0;
    while (remaining > 0 && safety++ < 20) {
      const clip = this.clip;
      const advance = Math.min(remaining, clip.duration - this.time);
      const before = this.time; this.time += advance; remaining -= advance;
      for (const event of clip.events || [])
        if (event.at > before && event.at <= this.time) this.onEvent({ ...event, animation: this.name });
      if (this.time < clip.duration) break;
      const finished = this.name;
      if (this.queue.length) {
        const next = this.queue.shift(); this.play(next.name, { ...next.options, clearQueue: false });
      } else if (this.loop) {
        this.time = 0;
      } else {
        this.play(clip.next || (this.posture === 'seated' ? 'seated_idle' : 'idle'));
      }
      this.onComplete(finished);
    }
  }
  draw(ctx, { x = this.x, y = this.y, scale = this.scale, shadow = true, debug = false } = {}) {
    const f = this.frame;
    ctx.save(); ctx.imageSmoothingEnabled = false;
    if (shadow) {
      ctx.fillStyle = 'rgba(0,0,0,.2)'; ctx.beginPath();
      ctx.ellipse(x, y - 2 * scale, 31 * scale, 10 * scale, 0, 0, Math.PI * 2); ctx.fill();
    }
    ctx.drawImage(this.images[f.page], f.x, f.y, f.w, f.h,
      Math.round(x - f.pivot.x * scale), Math.round(y - f.pivot.y * scale), f.w * scale, f.h * scale);
    if (debug) {
      ctx.strokeStyle = '#40e7df'; ctx.lineWidth = 1;
      ctx.strokeRect(x - f.pivot.x * scale, y - f.pivot.y * scale, f.w * scale, f.h * scale);
      ctx.beginPath(); ctx.moveTo(x-8,y); ctx.lineTo(x+8,y); ctx.moveTo(x,y-8); ctx.lineTo(x,y+8); ctx.stroke();
    }
    ctx.restore();
  }
}
