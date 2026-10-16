import {CHARACTER_LIGHT_RADIUS, fixturesFor} from './lighting-fixtures.js';
const W = 1536, H = 1024;
const clamp = (v, lo = 0, hi = 1) => Math.max(lo, Math.min(hi, v));
const smooth = v => { const t = clamp(v); return t*t*(3-2*t); };
const surface = () => { const c = document.createElement('canvas'); c.width = W; c.height = H; return c; };

export class RoomLighting {
  constructor(plate, index, id) {
    this.plate = plate; this.index = index; this.id = id; this.energy = 0; this.workMix=0;
    this.canvas = surface(); this.ctx = this.canvas.getContext('2d');
    this.darkPlate = surface(); const dark=this.darkPlate.getContext('2d');
    dark.drawImage(plate,0,0,W,H);dark.globalCompositeOperation='source-atop';
    dark.fillStyle='rgba(3,5,16,.79)';dark.fillRect(0,0,W,H);
    this.fixtures = fixturesFor(id);
    // Extract only the authored emitter rectangles, never a whole ceiling/floor colour band.
    for (const f of this.fixtures) {
      const [x,y,w,h] = f.rect, mask = document.createElement('canvas'); mask.width=w; mask.height=h;
      const g = mask.getContext('2d'); g.drawImage(plate,x,y,w,h,0,0,w,h);
      const pixels=g.getImageData(0,0,w,h), d=pixels.data;
      for(let k=0;k<d.length;k+=4){
        const hi=Math.max(d[k],d[k+1],d[k+2]),lo=Math.min(d[k],d[k+1],d[k+2]);
        const luminous=hi>150 && (hi-lo>35 || lo>235);
        const colourMatches=f.led || d[k]>d[k+2]*1.1 || lo>235;
        d[k+3] = luminous && colourMatches ? Math.round(d[k+3]*clamp((hi-150)/75)) : 0;
      }
      g.putImageData(pixels,0,0); f.mask=mask;
    }
  }
  update(dt, people) {
    const occupants=people.filter(p=>p.x>=this.index*W&&p.x<(this.index+1)*W);
    const working=occupants.some(p=>p.activity==='desk');
    const sleepers=occupants.filter(p=>p.activity==='sleep'||p.activity==='relax');
    const nightFixtures=new Set();
    for(const p of sleepers){
      const candidates=this.fixtures.filter(f=>!f.corridor&&!f.id.startsWith('ceiling')&&!f.id.startsWith('pillar'));
      candidates.sort((a,b)=>Math.hypot(p.x-this.index*W-a.sense[0],p.y-a.sense[1])-Math.hypot(p.x-this.index*W-b.sense[0],p.y-b.sense[1]));
      candidates.slice(0,2).forEach(f=>nightFixtures.add(f));
    }
    this.mode=working?'work':sleepers.length?'night':'local';
    this.workMix+=((working?1:0)-this.workMix)*(1-Math.exp(-dt*(working?2:1.2)));
    let sum=0;
    for(const f of this.fixtures){
      let target=0;
      for(const p of people){
        if(p.activity==='sleep'||p.activity==='relax')continue;
        const x=p.x-this.index*W;
        if(!f.corridor && (x<0 || x>W)) continue; // A bulkhead blocks another room's interior lights.
        const distance=Math.hypot(x-f.sense[0],p.y-f.sense[1]);
        target=Math.max(target,1-smooth((distance/CHARACTER_LIGHT_RADIUS-.55)/.45));
      }
      if(working)target=1;
      else if(sleepers.length){
        target=nightFixtures.has(f) ? .24 : 0;
        // A passer-by can still illuminate their own corridor strip during someone else's nap.
        if(f.corridor)for(const p of people){
          if(p.activity==='sleep'||p.activity==='relax')continue;
          const distance=Math.hypot(p.x-this.index*W-f.sense[0],p.y-f.sense[1]);
          target=Math.max(target,1-smooth((distance/CHARACTER_LIGHT_RADIUS-.55)/.45));
        }
      }
      if(this.id==='engine'||this.id==='server')target=Math.max(target,.035);
      f.level+=(target-f.level)*(1-Math.exp(-dt*(target>f.level?3:.9))); sum+=f.level;
    }
    this.energy=sum/this.fixtures.length; // Informational only; never used to brighten the room.
  }
  draw(ctx, time, people, detail = 1) {
    // Slow lighting does not need the starfield's frame rate. Reuse its crisp cached plate,
    // especially when the whole ship is visible and ten rooms share the same frame.
    const interval = 1 / (detail < .3 ? 8 : 24);
    if (this.lastRender !== undefined && time - this.lastRender < interval) {
      ctx.drawImage(this.canvas, this.index * W, 0); return;
    }
    this.lastRender = time;
    const g = this.ctx;
    g.clearRect(0,0,W,H);
    if(this.workMix>.001){
      g.drawImage(this.plate,0,0,W,H);g.globalCompositeOperation='source-atop';
      g.fillStyle=`rgba(3,5,16,${.79-.70*this.workMix})`;g.fillRect(0,0,W,H);g.globalCompositeOperation='source-over';
    }else g.drawImage(this.darkPlate,0,0);
    for (const f of this.fixtures) {
      if(f.level<.005)continue;
      const [x,y,rx,ry]=f.pool;
      // Every wash is anchored to its actual fixture. No moving, character-centred spotlight.
      if(!f.wash){
        // A static, crisp patch is made only when its lamp is first approached. Reusing it
        // avoids rebuilding full-room gradient masks every frame on wide monitors.
        const patch=document.createElement('canvas');patch.width=rx*2;patch.height=ry*2;
        const p=patch.getContext('2d');p.drawImage(this.plate,-(x-rx),-(y-ry));
        p.globalCompositeOperation='destination-in';p.translate(rx,ry);p.scale(rx,ry);
        const pool=p.createRadialGradient(0,0,.03,0,0,1);
        pool.addColorStop(0,'rgba(0,0,0,.93)');pool.addColorStop(.45,'rgba(0,0,0,.55)');pool.addColorStop(1,'rgba(0,0,0,0)');
        p.fillStyle=pool;p.fillRect(-1,-1,2,2);f.wash=patch;
      }
      g.globalAlpha=f.level;g.drawImage(f.wash,x-rx,y-ry);
    }
    g.globalAlpha=1;
    g.globalCompositeOperation = 'screen';
    this.fixtures.forEach((f,j)=>{
      const breath=.5-.5*Math.cos(time*Math.PI*2/6.8+this.index*.83+j*.57);
      g.globalAlpha=f.led ? .025+.045*breath+.7*f.level*(.84+.16*breath) : .85*f.level;
      g.drawImage(f.mask,f.rect[0],f.rect[1]);
    });
    g.globalAlpha = 1; g.globalCompositeOperation = 'source-over';
    ctx.drawImage(this.canvas, this.index * W, 0);
  }
  brightnessAt(worldX,y){
    const x=worldX-this.index*W;let illumination=.23+.70*this.workMix;
    for(const f of this.fixtures){const [px,py,rx,ry]=f.pool;
      const r=Math.hypot((x-px)/rx,(y-py)/ry);
      illumination+=(1-smooth(r))*.7*f.level;
    }
    return clamp(illumination,.23,1);
  }
  tintAvatar(ctx){
    ctx.save();ctx.globalCompositeOperation='source-atop';
    for(const f of this.fixtures){
      if(f.level<.01)continue;
      const [x,y,rx,ry]=f.pool,[r,g,b]=f.colour;
      ctx.save();ctx.translate(this.index*W+x,y);ctx.scale(rx*1.3,ry*1.3);
      const glow=ctx.createRadialGradient(0,0,0,0,0,1);
      glow.addColorStop(0,`rgba(${r},${g},${b},${.20*f.level})`);
      glow.addColorStop(.5,`rgba(${r},${g},${b},${.10*f.level})`);glow.addColorStop(1,`rgba(${r},${g},${b},0)`);
      ctx.fillStyle=glow;ctx.fillRect(-1,-1,2,2);ctx.restore();
    }
    ctx.restore();
  }
}
