function texturedTriangle(g,image,src,d){
  const [a,b,c]=src,cx=(d[0][0]+d[1][0]+d[2][0])/3,cy=(d[0][1]+d[1][1]+d[2][1])/3;
  g.save();g.beginPath();d.forEach(([x,y],i)=>{const l=Math.hypot(x-cx,y-cy)||1,x1=x+(x-cx)/l*.5,y1=y+(y-cy)/l*.5;if(i)g.lineTo(x1,y1);else g.moveTo(x1,y1)});g.closePath();g.clip();
  const x1=b[0]-a[0],y1=b[1]-a[1],x2=c[0]-a[0],y2=c[1]-a[1],det=x1*y2-x2*y1;
  const u1=d[1][0]-d[0][0],v1=d[1][1]-d[0][1],u2=d[2][0]-d[0][0],v2=d[2][1]-d[0][1];
  const A=(u1*y2-u2*y1)/det,B=(v1*y2-v2*y1)/det,C=(u2*x1-u1*x2)/det,D=(v2*x1-v1*x2)/det;
  g.transform(A,B,C,D,d[0][0]-A*a[0]-C*a[1],d[0][1]-B*a[0]-D*a[1]);
  const x=Math.min(a[0],b[0],c[0]),y=Math.min(a[1],b[1],c[1]),w=Math.max(a[0],b[0],c[0])-x,h=Math.max(a[1],b[1],c[1])-y;
  g.drawImage(image,x,y,w,h,x,y,w,h);g.restore();
}

// A painted whole posture, separated only at the parts that actually move.
// The standing/walking skeleton remains in Avatar Studio's BossRig.
export class PosePuppet {
  static async load(who, pose, version, folder = 'poses/') {
    const base = `${folder}${who}-${pose}`;
    try {
      const response = await fetch(`${base}.json?v=${version}`);
      if (!response.ok) return null;
      const spec = await response.json();
      let image = await new Promise((resolve, reject) => {
        const im = new Image(); im.onload = () => resolve(im); im.onerror = reject;
        im.src = `${spec.image ? folder + spec.image : base + '.webp'}?v=${version}`;
      });
      if(spec.workLean){
        // A coherent upper-body lean into the keyboard. The seated pelvis and feet
        // stay planted; head, sleeves, wrists and fingers share the same skinning.
        const {offset:[dx,dy],from,to}=spec.workLean;
        const smooth=u=>{u=Math.max(0,Math.min(1,u));return u*u*(3-2*u)};
        const weight=(y,x=600)=>smooth((to-y)/(to-from))*(1-smooth((y-405)/35)*(1-smooth((x-390)/200)));
        const padX=Math.ceil(Math.abs(dx)+16),padY=Math.ceil(Math.abs(dy)+16);
        const bent=document.createElement('canvas');bent.width=image.width+padX*2;bent.height=image.height+padY*2;
        const g=bent.getContext('2d');
        const mapped=([x,y])=>{const a=weight(y,x);return[x+padX+dx*a,y+padY+dy*a]};
        const triangle=(a,b,c)=>texturedTriangle(g,image,[a,b,c],[mapped(a),mapped(b),mapped(c)]);
        // Protect knees and thighs where their screen rows overlap the torso.
        // The mesh is prepared only once; playback keeps the original light finger taps.
        for(let y=0;y<to;y+=16)for(let x=0;x<image.width;x+=24){const xx=Math.min(image.width,x+24),yy=Math.min(to,y+16);triangle([x,y],[xx,y],[xx,yy]);triangle([x,y],[xx,yy],[x,yy]);}
        g.drawImage(image,0,to,image.width,image.height-to,padX,padY+to,image.width,image.height-to);
        const part=spec.parts[0];part.rect=[0,0,bent.width,bent.height];part.anchor=[part.anchor[0]+padX,part.anchor[1]+padY];
        for(const region of spec.typingHands?.regions||[]){const w=weight(region[1]+region[3]/2,region[0]+region[2]/2);region[0]+=padX+dx*w;region[1]+=padY+dy*w;}
        image=bent;
      }
      if (spec.decals?.length) {
        const painted = document.createElement('canvas'); painted.width = image.width; painted.height = image.height;
        const g = painted.getContext('2d'); g.drawImage(image, 0, 0);
        for (const d of spec.decals) {
          const decal = await new Promise((resolve, reject) => {
            const im = new Image(); im.onload = () => resolve(im); im.onerror = reject; im.src = `${d.image}?v=${version}`;
          });
          g.save(); g.translate(...d.at); g.rotate(d.angle || 0);
          g.drawImage(decal, ...d.rect, -d.size[0] / 2, -d.size[1] / 2, ...d.size); g.restore();
        }
        image = painted; // The established shirt wordmark deforms with the fabric, never floats over it.
      }
      return new PosePuppet(image, spec);
    } catch { return null; }
  }
  constructor(image, spec) { this.image = image; this.spec = spec; }
  bounds(roomX = 0) {
    const {position,scale,angle=0,parts}=this.spec;
    let x0=Infinity,y0=Infinity,x1=-Infinity,y1=-Infinity;
    for(const part of parts){
      const [, ,w,h]=part.rect,[ax,ay]=part.anchor,[px,py]=part.at;
      const height=part.stretchY ? part.stretchY.from+(h-part.stretchY.from)*part.stretchY.scale : h;
      const r=part.rotation||0;
      for(const [cx,cy] of [[0,0],[w,0],[w,height],[0,height]]){
        const u=(cx-ax)*(part.flipX?-1:1),v=cy-ay;
        const x=px+u*Math.cos(r)-v*Math.sin(r),y=py+u*Math.sin(r)+v*Math.cos(r);
        const wx=position[0]+scale*(x*Math.cos(angle)-y*Math.sin(angle));
        const wy=position[1]+scale*(x*Math.sin(angle)+y*Math.cos(angle));
        x0=Math.min(x0,wx);y0=Math.min(y0,wy);x1=Math.max(x1,wx);y1=Math.max(y1,wy);
      }
    }
    // Includes breathing, head dips, typing and anti-aliased hair without trimming artwork.
    const pad=18,x=Math.floor(x0-pad),y=Math.floor(y0-pad);
    return {x:roomX+x,y,w:Math.ceil(x1+pad)-x,h:Math.ceil(y1+pad)-y};
  }
  draw(ctx, roomX, time) {
    const { position, scale, angle = 0, parts, sleepBreath, doze, typingHands } = this.spec;
    ctx.save(); ctx.translate(roomX + position[0], position[1]);
    ctx.rotate(angle); ctx.scale(scale, scale);
    // The neck and hands inherit the body's breath so attachment points never separate.
    if (!sleepBreath && !doze) ctx.translate(0, Math.sin(time * 1.6) * 1.5);
    for (const part of parts) {
      const [sx, sy, sw, sh] = part.rect;
      const [ax, ay] = part.anchor;
      const [x, y] = part.at;
      const phase = time * (part.motion === 'typing' ? 9 : 1.6) + (part.phase || 0);
      // Pivot at the cuff: fingertip taps must never pull the wrist off its sleeve.
      // Each hand has its own phase, with occasional pauses between bursts of typing.
      const typing = part.motion === 'typing';
      const burst = Math.max(0, Math.sin(time * 1.2 + (part.phase || 0)) + 0.35) / 1.35;
      const turn = (part.rotation || 0) + (typing ? Math.sin(phase) * 0.022 * burst
        : part.motion === 'head' ? Math.sin(phase) * 0.012 : 0);
      ctx.save(); ctx.translate(x, y); ctx.rotate(turn);
      if (part.flipX) ctx.scale(-1, 1);
      // Hide overlapping paint below a joint without changing the source atlas or its hair.
      if (part.cutouts?.length) {
        ctx.beginPath(); ctx.rect(-ax, -ay, sw, sh);
        for (const [cx, cy, cw, ch] of part.cutouts) ctx.rect(cx - ax, cy - ay, cw, ch);
        ctx.clip('evenodd');
      }
      if (doze) {
        const d = doze, smooth = v => { const k = Math.max(0, Math.min(1, v)); return k * k * (3 - 2 * k); };
        const inhale = .5 - .5 * Math.cos((time + d.phase) * Math.PI * 2 / d.breathPeriod);
        const cycle = ((time + d.phase) % d.nodPeriod) - d.nodDelay, p = cycle / d.nodDuration;
        const dip = p < 0 || p > 1 ? 0 : p < .38 ? smooth(p / .38) : 1 - smooth((p - .38) / .62);
        ctx.translate(-ax, -ay);
        // Continuous row deformation bends the neck/upper torso together. The hip support,
        // hands and feet remain still; there is no separate head edge to open while nodding.
        for (let row = 0; row < sh; row += 10) {
          const height = Math.min(11.5, sh - row), mid = sy + row + height / 2;
          const upper = 1 - smooth((mid - d.neck[1]) / d.neckFade);
          const support = smooth((d.supportY - mid) / 170);
          const breath = -d.breathAmount * inhale * Math.exp(-.5 * ((mid - d.chestY) / d.chestSpread) ** 2) * support;
          ctx.save(); ctx.translate(d.neck[0] - sx, d.neck[1] - sy + breath + dip * d.sink * upper);
          ctx.rotate(dip * d.nodAngle * upper); ctx.translate(-(d.neck[0] - sx), -(d.neck[1] - sy));
          ctx.drawImage(this.image, sx, sy + row, sw, height, 0, row, sw, height); ctx.restore();
        }
      } else if (sleepBreath) {
        // A continuous, local chest expansion around the mattress support line. Feet and
        // resting hands stay supported; the assembled neck never separates from the body.
        const b = sleepBreath, inhale = 0.5 - 0.5 * Math.cos(time * Math.PI * 2 / b.period);
        for (let col = 0; col < sw; col += 8) {
          const width = Math.min(9, sw - col), u = (sx + col + width / 2 - b.centerX) / b.spread;
          const k = 1 + b.amount * inhale * Math.exp(-0.5 * u * u);
          const pivot = b.supportY - sy - ay;
          const dy = pivot + (-ay - pivot) * k;
          ctx.drawImage(this.image, sx + col, sy, width, sh, col - ax, dy, width, sh * k);
        }
      } else if (typingHands) {
        // Deform only fingertip regions of the coherent painting. A zero displacement
        // border keeps cuffs, wrists and the neck attached to the same body.
        ctx.save();ctx.beginPath();ctx.rect(-ax,-ay,sw,sh);
        for(const [hx,hy,hw,hh] of typingHands.regions)ctx.rect(hx-ax,hy-ay,hw,hh);
        ctx.clip('evenodd');ctx.drawImage(this.image,sx,sy,sw,sh,-ax,-ay,sw,sh);ctx.restore();
        for(const [hx,hy,hw,hh] of typingHands.regions){
          const mapped=([u,v])=>{const weight=Math.sin(Math.PI*(u-sx-hx)/hw)**2*Math.sin(Math.PI*(v-sy-hy)/hh)**2;
            return[u-sx-ax,v-sy-ay+Math.sin(time*Math.PI*2/typingHands.period+(u-sx-hx)*.035)*typingHands.amount*weight]};
          for(let col=0;col<hw;col+=12)for(let row=0;row<hh;row+=12){
            const x=sx+hx+col,y=sy+hy+row,xx=sx+hx+Math.min(hw,col+12),yy=sy+hy+Math.min(hh,row+12);
            for(const src of [[[x,y],[xx,y],[xx,yy]],[[x,y],[xx,yy],[x,yy]]])texturedTriangle(ctx,this.image,src,src.map(mapped));
          }
        }
      } else if (part.stretchY) {
        // Fit the painted shins to the seat-to-floor distance, keeping the torso and face uniform.
        const split = part.stretchY.from, lower = sh - split;
        ctx.drawImage(this.image, sx, sy, sw, split, -ax, -ay, sw, split);
        ctx.drawImage(this.image, sx, sy + split, sw, lower, -ax, split - ay, sw, lower * part.stretchY.scale);
      } else ctx.drawImage(this.image, sx, sy, sw, sh, -ax, -ay, sw, sh);
      ctx.restore();
    }
    ctx.restore();
  }
}

