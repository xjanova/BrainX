// A flat 2D renderer. Blender authors the skin weights and coherent posture clips.
// World coordinates are the existing room's painted pixels; there is no 3D camera.
const clamp=(v,a=0,b=1)=>Math.max(a,Math.min(b,v));
const smooth=v=>{v=clamp(v);return v*v*(3-2*v)};
const rotate=(x,y,a)=>[x*Math.cos(a)-y*Math.sin(a),x*Math.sin(a)+y*Math.cos(a)];
const angle=(a,b)=>Math.atan2(b[1]-a[1],b[0]-a[0])-Math.PI/2;

export class FlatSkin {
  constructor(){
    this.canvas=document.createElement('canvas');this.canvas.width=1536;this.canvas.height=1024;
    const gl=this.gl=this.canvas.getContext('webgl2',{alpha:true,antialias:true,premultipliedAlpha:true,preserveDrawingBuffer:true});
    if(!gl)throw Error('เบราว์เซอร์นี้ยังเปิดระบบแสดงผล 2D แบบใหม่ไม่ได้');
    const shader=(type,source)=>{const s=gl.createShader(type);gl.shaderSource(s,source);gl.compileShader(s);if(!gl.getShaderParameter(s,gl.COMPILE_STATUS))throw Error(gl.getShaderInfoLog(s));return s};
    const vertex=shader(gl.VERTEX_SHADER,`#version 300 es
      in vec2 aPosition;in vec2 aUV;in vec3 aNormal;in float aAlpha;
      out vec2 uv;out vec2 world;out vec3 normal;out float opacity;
      void main(){world=aPosition;uv=aUV;normal=aNormal;opacity=aAlpha;
        gl_Position=vec4(aPosition.x/768.0-1.0,1.0-aPosition.y/512.0,0,1);}`);
    const fragment=shader(gl.FRAGMENT_SHADER,`#version 300 es
      precision highp float;
      uniform sampler2D image;uniform vec3 lamps[8];uniform vec4 colours[8];uniform float radii[8];
      uniform float ambient;uniform float gain;uniform float relight;uniform float roughness;
      in vec2 uv;in vec2 world;in vec3 normal;in float opacity;out vec4 pixel;
      void main(){vec4 tex=texture(image,uv);float alpha=tex.a*opacity;if(alpha<.012)discard;
        vec3 rgb=pow(max(tex.rgb,vec3(0)),vec3(2.2));vec3 light=vec3(ambient);
        vec3 n=normalize(normal);
        for(int i=0;i<8;i++){
          vec3 delta=vec3(lamps[i].x-world.x,world.y-lamps[i].y,lamps[i].z);
          float distance=length(delta.xy);float falloff=1.0-smoothstep(radii[i]*.15,radii[i],distance);
          vec3 direction=normalize(delta);float diffuse=max(0.0,dot(n,direction));
          vec3 halfDirection=normalize(direction+vec3(0,0,1));
          float highlight=pow(max(0.0,dot(n,halfDirection)),mix(35.0,8.0,roughness))*(1.0-roughness)*.20;
          light+=colours[i].rgb*colours[i].a*falloff*(.12+diffuse*.88+highlight);
        }
        rgb*=mix(vec3(1),clamp(light,vec3(.28),vec3(1.42)),relight)*gain;
        rgb=pow(clamp(rgb,0.0,1.0),vec3(1.0/2.2));pixel=vec4(rgb*alpha,alpha);}`);
    const program=this.program=gl.createProgram();gl.attachShader(program,vertex);gl.attachShader(program,fragment);gl.linkProgram(program);
    if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw Error(gl.getProgramInfoLog(program));
    gl.useProgram(program);this.buffer=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,this.buffer);
    let offset=0;
    for(const [name,size]of[['aPosition',2],['aUV',2],['aNormal',3],['aAlpha',1]]){const at=gl.getAttribLocation(program,name);gl.enableVertexAttribArray(at);gl.vertexAttribPointer(at,size,gl.FLOAT,false,32,offset);offset+=size*4;}
    this.uniform={};for(const key of ['image','lamps','colours','radii','ambient','gain','relight','roughness'])this.uniform[key]=gl.getUniformLocation(program,key+(key==='lamps'||key==='colours'||key==='radii'?'[0]':''));
    gl.enable(gl.BLEND);gl.blendFunc(gl.ONE,gl.ONE_MINUS_SRC_ALPHA);gl.disable(gl.DEPTH_TEST);
    this.textures=new Map();this.grids=new Map();this.drawCalls=0;
  }
  texture(image){
    if(this.textures.has(image))return this.textures.get(image);
    const gl=this.gl,tex=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,tex);
    gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL,false);gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL,false);
    gl.texImage2D(gl.TEXTURE_2D,0,gl.RGBA,gl.RGBA,gl.UNSIGNED_BYTE,image);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
    this.textures.set(image,tex);return tex;
  }
  begin(fixtures,center,relight=true){
    const gl=this.gl;gl.viewport(0,0,1536,1024);gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT);gl.useProgram(this.program);
    const active=fixtures.filter(f=>f.level>.004).sort((a,b)=>b.level/(120+Math.hypot(b.at[0]-center[0],b.at[1]-center[1]))-a.level/(120+Math.hypot(a.at[0]-center[0],a.at[1]-center[1]))).slice(0,8);
    const lamps=new Float32Array(24),colours=new Float32Array(32),radii=new Float32Array(8).fill(1);
    active.forEach((f,i)=>{lamps.set([f.at[0],f.at[1],f.corridor?190:230],i*3);colours.set([...f.colour.map(n=>n/255),f.level*(f.corridor?.30:.48)],i*4);radii[i]=f.corridor?390:670;});
    gl.uniform3fv(this.uniform.lamps,lamps);gl.uniform4fv(this.uniform.colours,colours);gl.uniform1fv(this.uniform.radii,radii);
    gl.uniform1f(this.uniform.ambient,active.length?.38:.30);gl.uniform1f(this.uniform.relight,relight?1:0);gl.uniform1i(this.uniform.image,0);
    this.drawCalls=0;
  }
  draw(image,points,triangles,sourceUV,normals,alpha=null,gain=1,roughness=.82){
    const gl=this.gl,count=triangles.length*3;let buffer=this.vertexBuffer;
    if(!buffer||buffer.length<count*8)this.vertexBuffer=buffer=new Float32Array(count*8);
    let j=0;
    for(const face of triangles)for(const i of face){const p=points[i],uv=sourceUV[i],n=normals[i]||[0,0,1];buffer.set([p[0],p[1],uv[0]/image.width,uv[1]/image.height,n[0],n[1],n[2],alpha?alpha[i]:1],j);j+=8;}
    gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,this.texture(image));gl.uniform1f(this.uniform.gain,gain);gl.uniform1f(this.uniform.roughness,roughness);
    gl.bindBuffer(gl.ARRAY_BUFFER,this.buffer);gl.bufferData(gl.ARRAY_BUFFER,buffer.subarray(0,j),gl.DYNAMIC_DRAW);gl.drawArrays(gl.TRIANGLES,0,count);this.drawCalls++;
  }
  grid(key,w,h,step=28){
    key=`${key}:${w}:${h}:${step}`;
    if(this.grids.has(key))return this.grids.get(key);
    const nx=Math.ceil(w/step),ny=Math.ceil(h/step),uv=[],triangles=[];
    for(let y=0;y<=ny;y++)for(let x=0;x<=nx;x++)uv.push([x*w/nx,y*h/ny]);
    for(let y=0;y<ny;y++)for(let x=0;x<nx;x++){const i=y*(nx+1)+x;triangles.push([i,i+1,i+nx+2],[i,i+nx+2,i+nx+1]);}
    const mesh={uv,triangles};this.grids.set(key,mesh);return mesh;
  }
  rig(rig,meshes,scale,originX=0){
    if(!rig.J)return;
    this.lastHands={};
    const J=rig.J;
    const pilot=!meshes.character,shoeScale=meshes.shoeScale??.78;
    const toWorld=([x,y])=>[rig.x-originX+x*scale*rig.Fv,rig.y+y*scale];
    const normalWorld=(n,a)=>{const [x,y]=rotate(n[0],-n[1],a);return[x*rig.Fv,-y,n[2]]};
    const drawPiece=(key,anchor,at,rotation,k=1,gain=1,frontHem=false,mirror=1,lengthRatio=1)=>{
      const p=(key==='head'&&rig.hasFaces?rig.P['head_'+rig.mood]:rig.P[key])||rig.P[key],[rx,ry,w,h]=p.rect,m=this.grid(key,w,h,key==='skirt'||key==='foot'?6:key==='torso'?14:key==='head'?18:28),j=p.j[anchor];
      const points=[],uv=[],normals=[],alphas=[];
      for(const [x,y]of m.uv){
        let sx=x,sy=y;
        if(key.endsWith('Logo')&&rig.Fv<0){
          const c=p.j.c,t0=p.j.t0,t1=p.j.t1,phi=(t0&&t1?Math.atan2(t1[1]-t0[1],t1[0]-t0[0]):0)+Math.PI/2,dx=x-c[0],dy=y-c[1];
          sx=c[0]+Math.cos(2*phi)*dx+Math.sin(2*phi)*dy;sy=c[1]+Math.sin(2*phi)*dx-Math.cos(2*phi)*dy;
        }
        let u=(sx-j[0])*k*mirror,v=(sy-j[1])*k;
        if(lengthRatio!==1){const dx=Math.cos(p.axis)*mirror,dy=Math.sin(p.axis),along=u*dx+v*dy;u+=along*(lengthRatio-1)*dx;v+=along*(lengthRatio-1)*dy;}
        if(key==='torso'){
          const displacement=rig.bustDisp(x,y,12*Math.tanh(rig.sp.bust.x/18));
          u+=displacement[0];v+=displacement[1];
        }
        if(key==='skirt')v-=(meshes.waistTuck??24)*(1-smooth(y/200));
        const r=rotate(u,v,rotation);let pos=[at[0]+r[0],at[1]+r[1]];
        if(key==='head'){
          // Lower central neck shares torso motion; side hair remains attached to the head.
          const weight=smooth((y-(j[1]-20))/58)*(1-smooth((Math.abs(x-j[0])-32)/55));
          const support=rotate(u,v,J.tRot);pos=[pos[0]*(1-weight)+(at[0]+support[0])*weight,pos[1]*(1-weight)+(at[1]+support[1])*weight];
        }
        if(key==='torso'){
          const weight=(1-smooth((y-20)/100))*(1-smooth(Math.abs(x-p.j.neck[0])/100))*.36;
          const head=rotate(pos[0]-J.neck[0],pos[1]-J.neck[1],J.hRot-J.tRot);pos=[pos[0]*(1-weight)+(J.neck[0]+head[0])*weight,pos[1]*(1-weight)+(J.neck[1]+head[1])*weight];
          const breath=Math.sin(rig.t*Math.PI*2/5.6)*2.4*Math.exp(-.5*((y-170)/100)**2);pos[1]+=breath;
        }
        const nx=clamp((x-w*.52)/(w*.58),-.78,.78)*.55,ny=clamp((h*.48-y)/(h*.65),-.7,.7)*.20;
        points.push(toWorld(pos));uv.push([rx+x,ry+y]);normals.push(normalWorld([nx*mirror,ny,Math.sqrt(1-nx*nx-ny*ny)],rotation));
        // The rear lining is behind the thighs; only the curved front hem covers them.
        const mask=meshes.skirtMask??(pilot?[218,48,.514,1.7]:null);
        const edge=mask?mask[0]+mask[1]*Math.pow(clamp(Math.abs(x-w*mask[2])/(w*.49)),mask[3]):h+10;
        const shoeFront=Math.max(smooth((y-(p.j.a?.[1]+(meshes.character==='mai'?14:26)+.55*(x-(p.j.a?.[0]??76))))/4),smooth((x-(p.j.a?.[0]+29))/20));
        alphas.push(frontHem?(key==='foot'?shoeFront:1-smooth((y-edge+1.5)/3)):1);
      }
      this.draw(p.img||rig.img,points,m.triangles,uv,normals,frontHem?alphas:null,gain,key==='head'?.45:.86);
    };
    const strip=(key,a,side,gain=1)=>{
      const m=meshes.parts[key],isArm=m.kind==='arm',p=rig.P[m.sourceKey||key];
      const S=isArm?a.S:a.H,E=isArm?a.E:a.K,W=isArm?a.W:a.A;
      const [l1,l2]=meshes.lengths?.[isArm?'arm':'leg']||(isArm?[259,162]:[312.32,276.93]);
      const upper=angle(S,E),lower=angle(E,W),hand=isArm?lower+(a.hand||0):(a.pitch||0);
      const bones={support:{at:S,origin:0,angle:J.tRot,k:1},upper:{at:S,origin:0,angle:upper,k:Math.hypot(E[0]-S[0],E[1]-S[1])/l1},lower:{at:E,origin:l1,angle:lower,k:Math.hypot(W[0]-E[0],W[1]-E[1])/l2},hand:{at:W,origin:l1+l2,angle:hand,k:1}};
      const points=[],uv=[],normals=[],alphas=[];
      for(let i=0;i<m.rest.length;i++){
        let [u,v]=m.rest[i];if(key==='hand'&&side==='F'){u=-u;v=l1+l2+(v-l1-l2)*.82;}
        let cx=0,cy=0,c=0,s=0;
        for(const [name,weight]of Object.entries(m.weights[i])){
          if(weight<.00001)continue;const b=bones[name],co=Math.cos(b.angle),si=Math.sin(b.angle);
          cx+=(b.at[0]-(v-b.origin)*si*b.k)*weight;cy+=(b.at[1]+(v-b.origin)*co*b.k)*weight;c+=co*weight;s+=si*weight;
        }
        const length=Math.hypot(c,s)||1;c/=length;s/=length;
        points.push(toWorld([cx+u*c,cy+u*s]));
        const [x,y]=m.uv[i];uv.push([p.rect[0]+x,p.rect[1]+y]);
        let alpha=1;
        if(isArm){
          if(m.blend.top)alpha*=smooth((y/m.blend.top-.35)/.65);
          if(m.blend.bottom)alpha*=1-smooth((y-(p.rect[3]-m.blend.bottom))/m.blend.bottom);
        }else{
          // Both images now share the same cross-section at the knee.
          if(key==='thigh')alpha*=1-smooth((v-l1)/40);
          if(key==='shin')alpha*=smooth((v-(l1-40))/40)*(1-smooth((v-(l1+l2+(meshes.character==='mai'?12:24)))/16));
        }
        alphas.push(alpha);
        const nx=clamp(u/(p.rect[2]*.58),-.82,.82)*.67;
        normals.push(normalWorld([nx,0,Math.sqrt(1-nx*nx)],Math.atan2(s,c)));
      }
      this.draw(rig.img,points,m.triangles,uv,normals,alphas,gain,key==='hand'?.48:.86);
    };
    const hand=(a,side,gain)=>{
      const isF=side==='F',fore=Math.atan2(a.W[1]-a.E[1],a.W[0]-a.E[0]),pz=rig.pose,tablet=isF?pz.tablet:0;
      const sprite=(key,mirror,k=1)=>{const p=rig.P[key],lengthRatio=isF?.82:1;this.lastHands[side]={key,asset:p.asset||key,mirror,scale:k,lengthRatio};drawPiece(key,'wrist',a.W,fore-(mirror>0?p.axis:Math.PI-p.axis)+(a.hand||0),k,gain,false,mirror,lengthRatio)};
      if(rig.gait.moving&&!rig.mocap&&rig.P.walkHand&&tablet<.05&&pz.handsUp<.05){if(isF&&rig.P.farHand)sprite('farHand',1,rig.P.farHand.scale*.8);else sprite('walkHand',isF?-1:1,rig.P.walkHand.scale);return;}
      const busy=isF?Math.max(pz.faceHand,pz.point,pz.open,pz.talkF,pz.palmF,pz.clasp,pz.hipHandF,pz.type):Math.max(pz.hipHand,pz.lap,pz.openN,pz.talkN,pz.clasp,pz.type);
      if(isF&&rig.P.farHand&&busy<.05&&tablet<.05&&pz.handsUp<.05){sprite('farHand',1,rig.P.farHand.scale);return;}
      const shapes=[['chestHand',Math.max(pz.handsUp,1-busy),isF?1:-1]];
      if(isF)shapes.push(['peaceHand',pz.faceHand,-1],['pointHand',pz.point,1],['openHand',pz.open,-1],['talkHand',pz.talkF,-1],['palmHand',pz.palmF,1]);
      else shapes.push(['openHand',pz.openN,1],['talkHand',pz.talkN,1]);
      const chosen=tablet<.5?shapes.filter(([key,w])=>rig.P[key]&&w>.35).sort((a,b)=>b[1]-a[1])[0]:null;
      // Claude's original gesture atlas is larger than her arm rig.
      // Keep the wrist anchored while matching its near hand to the far palm.
      if(chosen){sprite(chosen[0],chosen[2],meshes.character==='claude'&&!isF?.84:1);return;}
      if(rig.P.tabletHand&&tablet>.5){sprite('tabletHand',1);return;}
      this.lastHands[side]={key:'hand',mirror:isF?-1:1,scale:rig.cfg.handK||1,lengthRatio:isF?.82:1};strip('hand',a,side,gain);
    };
    const arm=(a,side,gain)=>{strip('upperArm',a,side,gain);hand(a,side,gain);strip('foreArm',a,side,gain)};
    const leg=(a,side,gain)=>{
      const pitch=(a.pitch||0)+clamp((a.lift||0)/110,0,.3);
      drawPiece('foot','a',a.A,pitch,shoeScale,gain);
      strip('thigh',a,side,gain);strip('shin',a,side,gain);
      drawPiece('foot','a',a.A,pitch,shoeScale,gain,true);
    };
    // One continuous hair sheet follows the existing damped chain, with a visible trailing edge.
    const drawHair=()=>{
    const p=rig.P.tail,[rx,ry,w,h]=p.rect,tie=p.j.tie,m=this.grid('tail',w,h,28);
    const points=[],uv=[],normals=[];
    for(const [x,y]of m.uv){
      const f=clamp((y-tie[1])/(h-tie[1]))*(rig.tail.n-1),i=Math.min(rig.tail.n-2,Math.floor(f)),t=f-i;
      const a=rig.tail.p[i],b=rig.tail.p[i+1],dx=b[0]-a[0],dy=b[1]-a[1],length=Math.hypot(dx,dy)||1;
      const row=y/(p.centre?.step||8),ci=Math.floor(row),centers=p.centre?.x;
      const center=centers?centers[Math.min(ci,centers.length-1)]*(1-row%1)+centers[Math.min(ci+1,centers.length-1)]*(row%1):tie[0];
      const u=x-(tie[0]+(center-tie[0])*smooth((y-tie[1])/80)),trail=smooth((y-tie[1])/(h-tie[1]))*(pilot?38:12);
      let pos=[a[0]+dx*t+dy/length*u-trail,a[1]+dy*t-dx/length*u];
      if(y<tie[1]){const q=rotate(u,y-tie[1],J.hRot);pos=[J.tie[0]+q[0],J.tie[1]+q[1]];}
      points.push(toWorld(pos));uv.push([rx+x,ry+y]);const nx=clamp(u/(w*.62),-.8,.8)*.6;normals.push([nx*rig.Fv,0,Math.sqrt(1-nx*nx)]);
    }
    this.draw(rig.img,points,m.triangles,uv,normals,null,.94,.35);
    };
    if(!rig.cfg.hair.hidden&&!rig.cfg.hair.front)drawHair();
    if(rig.P.cape){const p=rig.P.torso.j,c=rotate(p.cape[0]-p.waist[0],p.cape[1]-p.waist[1],J.tRot);drawPiece('cape','top',[J.waist[0]+c[0],J.waist[1]+c[1]],J.tRot*.6);}
    arm(J.armF,'F',.84);
    drawPiece('skirt','waist',J.pelvis,J.pRot+rig.sp.skirt.x*.45);
    leg(J.legs[1],'F',.87);leg(J.legs[0],'N',1);
    drawPiece('skirt','waist',J.pelvis,J.pRot+rig.sp.skirt.x*.45,1,1,true);
    drawPiece('torso','waist',J.waist,J.tRot);
    if(rig.P.torsoLogo){const p=rig.P.torsoLogo,T=rig.P.torso.j.waist,d=rig.bustDisp(T[0]-p.j.waist[0]+p.j.c[0],T[1]-p.j.waist[1]+p.j.c[1],12*Math.tanh(rig.sp.bust.x/18)),r=rotate(...d,J.tRot);drawPiece('torsoLogo','waist',[J.waist[0]+r[0],J.waist[1]+r[1]],J.tRot);}
    drawPiece('head','neck',J.neck,J.hRot,rig.cfg.headScale||1);
    if(rig.hasFaces){const eyes=rig.eyePatch();if(eyes)drawPiece(eyes,'neck',J.neck,J.hRot,rig.cfg.headScale||1);if(rig.mouthShape)drawPiece(rig.mouthShape,'neck',J.neck,J.hRot,rig.cfg.headScale||1);}
    if(rig.P.bow){drawPiece('bow','knot',J.tie,J.hRot+rig.sp.bow.x,rig.cfg.headScale||1);if(rig.P.bowLogo)drawPiece('bowLogo','knot',J.tie,J.hRot+rig.sp.bow.x,rig.cfg.headScale||1);}
    if(!rig.cfg.hair.hidden&&rig.cfg.hair.front)drawHair();
    arm(J.armN,'N',1);
  }
  paintedPose(puppet,mode,time,roomX=0,originX=0){
    this.poseCache??=new WeakMap();let base=this.poseCache.get(puppet);
    if(!base){
      const b=puppet.bounds(0),image=document.createElement('canvas');image.width=b.w*2;image.height=b.h*2;
      const ctx=image.getContext('2d');ctx.setTransform(2,0,0,2,-b.x*2,-b.y*2);puppet.draw(ctx,0,0);
      const mesh=this.grid('assembled-pose',image.width,image.height,24),hands=[];
      const spec=puppet.spec;
      for(const p of spec.parts){
        if(p.motion==='typing'){const [, ,w,h]=p.rect;hands.push([spec.position[0]+(p.at[0]+(w*.5-p.anchor[0])*(p.flipX?-1:1))*spec.scale,spec.position[1]+(p.at[1]+h*.55-p.anchor[1])*spec.scale,w*spec.scale*.55,h*spec.scale*.42]);}
        for(const [x,y,w,h]of spec.typingHands?.regions||[]){hands.push([spec.position[0]+(p.at[0]+x+w*.5-p.anchor[0])*spec.scale,spec.position[1]+(p.at[1]+y+h*.5-p.anchor[1])*spec.scale,w*spec.scale*.5,h*spec.scale*.5]);}
      }
      base={b,image,mesh,hands,position:[...spec.position]};this.poseCache.set(puppet,base);
    }
    const {b,image,mesh,hands}=base,points=[],normals=[],spec=puppet.spec;
    const phase=time*Math.PI*2/5.6,inhale=.5-.5*Math.cos(phase),neck=[b.x+b.w*.52,b.y+b.h*.29];
    const nod=mode==='relax'?.038*(.5-.5*Math.cos(time*Math.PI*2/12))**5:mode==='desk'?.008*Math.sin(time*.8):0;
    for(const [u,v]of mesh.uv){
      let x=b.x+u/2,y=b.y+v/2;const nx=(x-b.x)/b.w,ny=(y-b.y)/b.h;
      const support=smooth((spec.floorY-y)/Math.max(30,b.h*.35));
      if(mode==='sleep')y-=1.6*inhale*Math.exp(-.5*((nx-.65)/.18)**2)*support;
      else{
        const head=1-smooth((ny-.28)/.35),r=rotate(x-neck[0],y-neck[1],nod*head);
        x=neck[0]+r[0];y=neck[1]+r[1]-1.1*inhale*Math.exp(-.5*((ny-.42)/.18)**2)*support;
        if(mode==='desk')hands.forEach(([hx,hy,rx,ry],i)=>{const q=((x-hx)/rx)**2+((y-hy)/ry)**2;if(q<1)y+=.45*Math.sin(time*9+i*1.9)*(1-q)**2;});
      }
      points.push([roomX-originX+x+spec.position[0]-base.position[0],y+spec.position[1]-base.position[1]]);const normalX=(nx-.5)*.65,normalY=(.45-ny)*.22;normals.push([normalX,normalY,Math.sqrt(1-normalX**2-normalY**2)]);
    }
    this.draw(image,points,mesh.triangles,mesh.uv,normals,null,1,.68);
  }
  pose(data,image,time){
    const frame=((time%data.duration)/data.duration)*data.frames.length,lo=Math.floor(frame),hi=(lo+1)%data.frames.length,t=frame-lo;
    const a=data.frames[lo],b=data.frames[hi],points=[],normals=[],uv=data.uv,k=data.scale,[px,py]=data.position,[ax,ay]=data.anchor;
    for(let i=0;i<uv.length;i++){
      points.push([px+((a[i*2]*(1-t)+b[i*2]*t)/data.quantization-ax)*k,py+((a[i*2+1]*(1-t)+b[i*2+1]*t)/data.quantization-ay)*k]);
      normals.push(data.normals.slice(i*3,i*3+3));
    }
    this.draw(image,points,data.triangles,uv,normals,null,1,.64);
  }
}
