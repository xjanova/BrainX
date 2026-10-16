// Module-image coordinates. `at` is the painted emitter; `sense` is its floor footprint.
// A person can trigger only the fixtures inside this radius, not the entire room.
export const CHARACTER_LIGHT_RADIUS = 300;
const ROOM_COLOURS = {boss:[65,185,255],claude:[255,172,85],lounge:[255,185,95],codex:[215,90,255],grok:[170,235,65],cluadex:[195,125,255],mai:[200,150,255],bridge:[65,170,255],engine:[55,205,255],server:[65,165,255]};
const lamp = (id, at, rect, sense, pool, led = false) => ({id, at, rect, sense, pool, led});
const ceiling = (id, x, y, w = 240) => lamp(id, [x,y], [x-w/2,y-15,w,30], [x,650], [x,325,230,335], true);
const pendant = (id, x, y) => lamp(id, [x,y], [x-42,y-52,84,104], [x,625], [x,y+120,205,250]);
const ring = () => lamp('ceiling-ring',[770,95],[490,14,550,132],[770,625],[770,290,290,330]);
const accent = (id, x, y, w, h, fy = 625) => lamp(id, [x,y], [x-w/2,y-h/2,w,h], [x,fy], [x,y+75,180,210], true);
const SPECIAL = {
  boss: [ceiling('ceiling-left',365,84,270), ceiling('ceiling-center',770,48,350), ceiling('ceiling-right',1260,78,250), pendant('desk-lantern',165,251), accent('bed-left',710,260,45,200), accent('bed-base',870,426,310,25)],
  claude: [ceiling('ceiling-left',280,90,250), ring(), ceiling('ceiling-right',1235,76,260), pendant('desk-lantern',352,173), pendant('window-lantern',1118,142), pendant('right-lantern',1310,117), pendant('floor-lantern',645,400)],
  lounge: [ceiling('ceiling-left',265,109,280), ring(), ceiling('ceiling-right',1250,107,260), pendant('pendant-left',365,237), pendant('pendant-right',1182,237), pendant('low-left',275,392), pendant('low-right',1278,393)],
  codex: [ceiling('ceiling-left',370,87,270), ceiling('ceiling-center',770,48,350), ceiling('ceiling-right',1260,78,250), accent('bed-left',780,311,40,230), accent('bed-base',962,488,310,24), accent('desk-edge',340,420,260,25)],
  grok: [ceiling('ceiling-left',350,87,260), ceiling('ceiling-center',770,47,350), ceiling('ceiling-right',1240,79,270), pendant('shelf-lantern',704,164), pendant('right-lantern',1332,146), accent('bed-base',1044,371,330,25)],
  cluadex: [ceiling('ceiling-left',350,87,270), ceiling('ceiling-center',770,47,350), ceiling('ceiling-right',1240,79,270), pendant('curtain-left',770,203), pendant('curtain-right',1176,202), accent('desk-base',425,456,300,25)],
  mai: [ceiling('ceiling-left',350,89,260), ring(), ceiling('ceiling-right',1225,79,280), pendant('desk-lantern',144,277), pendant('right-lantern',1370,197), accent('bed-top',1100,239,310,30), accent('bed-base',1080,481,310,26)],
  bridge: [ceiling('ceiling-left',355,77,250), ceiling('ceiling-center',770,45,350), ceiling('ceiling-right',1240,77,270), accent('console-left',284,400,180,25), accent('console-center',798,422,200,30), accent('console-right',1268,400,180,25)],
  engine: [ceiling('ceiling-left',360,77,270), ceiling('ceiling-center',770,46,350), ceiling('ceiling-right',1240,77,270), accent('reactor',770,349,180,300), accent('machine-left',410,340,150,100), accent('machine-right',1125,340,150,100)],
  server: [ceiling('ceiling-left',360,77,270), ceiling('ceiling-center',770,46,350), ceiling('ceiling-right',1240,77,270), accent('core',770,325,150,240), accent('rack-left',310,322,38,250), accent('rack-right',1225,322,38,250)],
};
export function fixturesFor(id) {
  const corridor = [[0,335],[350,285],[649,285],[950,285],[1250,285]].map(([x,w],i) => ({
    ...lamp('corridor-'+i, [x+w/2,690], [x,678,w,26], [x+w/2,800], [x+w/2,752,185,135], true), corridor:true,
  }));
  // The two bulkhead lamps and corridor strips are shared by the module architecture.
  const pillars = [60,1474].map((x,i) => lamp('pillar-'+i, [x,260], [x-14,130,28,258], [x,690], [x,400,155,310]));
  return [...corridor, ...pillars, ...(SPECIAL[id] || [])].map(f => ({...f,level:0,colour:f.led&&!f.corridor ? ROOM_COLOURS[id] || [150,185,255] : [255,180,90]}));
}
