// Shared restrained motion, with character-specific geometry and hair settings.
export function tuneCrew(rig,meshes,mode=()=>null){
  rig.cfg={...rig.cfg,hair:{...rig.cfg.hair}};
  if(meshes.character==='mai')rig.cfg.headScale=1.24;
  if(!meshes.character)rig.cfg.hair.dir=[-.19,.982];
  if(meshes.character==='claude')rig.off.neck[1]+=6;
  rig.bustGain={boss:.75,claude:.65,codex:.8,grok:.65,mai:.7}[meshes.character]??.85;
  rig.sp.bust.c=9;
  if(meshes.character==='mai'){rig.P.foot={...rig.P.foot,j:{...rig.P.foot.j,a:[50,38]}};rig.L.ankleH=rig.P.foot.j.heel[1]-38;}
  rig.L.ankleH*=meshes.shoeScale??.78;rig.carrying=false;
  const native=rig.targetPose.bind(rig);
  rig.targetPose=function(t){
    const p=native(t);p.hipHand=0;p.tablet=0;
    if(this.gait.moving){
      const phase=this.gait.phase*Math.PI*2,energy=Math.min(1,this.speed/110);
      p.armN={th:-.04-.17*energy*Math.cos(phase),el:.14+.025*Math.sin(phase-.4),hand:-.025*Math.cos(phase-.3)};
      p.armF={th:-.04+.17*energy*Math.cos(phase),el:.14-.025*Math.sin(phase-.4),hand:.025*Math.cos(phase-.3)};
      p.hNod=.024*energy*Math.sin(phase*2-.45);
    }else if(this.state==='idle'){p.armN={th:-.1,el:.15,hand:0};p.armF={th:.08,el:.14,hand:0};p.tRot=-.01+.006*Math.sin(t*Math.PI*2/5.6);p.hRot=.018*Math.sin(t*.5);}
    if(mode()==='joints'){p.armN={th:.1+.18*Math.sin(t*.8),el:.15+.95*(.5+.5*Math.sin(t*.9)),hand:.05*Math.sin(t*.9)};p.armF={th:.08-.16*Math.sin(t*.8),el:.2+.65*(.5+.5*Math.sin(t*.9+1.5)),hand:-.04*Math.sin(t*.9)};p.hRot=.11*Math.sin(t*.7);}
    return p;
  };
}
