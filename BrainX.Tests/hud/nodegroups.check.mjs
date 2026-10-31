// Which points are one point, and where code sits in both themes. Runs without a browser:
//     node BrainX.Tests/hud/nodegroups.check.mjs
// Exit 0 = every check passed. Same contract as the C# harness.

import { groupNotes, gatherGroups } from '../../BrainX.Client/wwwroot/universe/nodegroups.js';
import { buildUniverse } from '../../BrainX.Client/wwwroot/universe/layout.js';
import { buildBrainGraph, fiberRoute } from '../../BrainX.Client/wwwroot/universe/brainlayout.js';

let passed = 0, failed = 0;
function check(name, ok, detail) {
    if (ok) { passed++; return; }
    failed++;
    console.log(`FAIL ${name}${detail !== undefined ? `\n     ${JSON.stringify(detail)}` : ''}`);
}

const dist = (a, b) => Math.hypot(a.x - b.x, a.y - b.y, a.z - b.z);
const len = p => Math.hypot(p.x, p.y, p.z);

let id = 0;
function note(title, tags = [], extra = {}) {
    return { id: `n${id++}`, title, tags, category: 'Programming', scope: '', brightness: 0.5, wordCount: 10,
        local: { u: 0, v: 0, n: 0 }, position: { x: 0, y: 0, z: 0 }, ...extra };
}

// ── what counts as the same subject ──
{
    const sky = [
        note('2026-10-19 BrainX auto-digest 55bdf9b5'), note('2026-10-10 BrainX auto-digest f7906455'),
        note('CHANGELOG', [], { scope: 'a' }), note('CHANGELOG', [], { scope: 'b' }),
    ];
    groupNotes(sky);
    check('a title that differs only by date and hash is one subject', sky[0].groupHead != null && sky[0].groupHead === sky[1].groupHead, sky.slice(0, 2));
    check('the label reads without the date and hash', sky[0].groupLabel === 'BrainX auto-digest', sky[0].groupLabel);
    check('the same title in two projects is two subjects', sky[2].groupHead == null && sky[3].groupHead == null);
}
{
    const sky = [note('a', ['universe']), note('b', ['universe']), note('c', ['universe']), note('d', ['Universe']), note('e', ['other'])];
    groupNotes(sky);
    check('three notes under one tag gather', sky.slice(0, 4).every(n => n.groupHead === sky[0].groupHead && n.groupHead != null));
    check('tags compare without case', sky[3].groupLabel === '#universe', sky[3].groupLabel);
    check('a note on its own stays on its own', sky[4].groupHead == null);
}
{
    const sky = [note('a', ['x']), note('b', ['x'])];
    groupNotes(sky);
    check('two notes do not make a tag group', sky[0].groupHead == null);
}
{
    const sky = [1, 2, 3, 4].map(n => note(`t${n}`, ['2026-09-14']))
        .concat([1, 2, 3, 4].map(n => note(`s${n}`, ['blocked'])));
    groupNotes(sky);
    check('a date is not a topic', sky[0].groupHead == null);
    check('a status is not a topic', sky[4].groupHead == null);
}
{
    const sky = Array.from({ length: 30 }, (_, i) => note(`p${i}`, ['big-project']));
    groupNotes(sky);
    check('a tag wider than a topic gathers nothing', sky.every(n => n.groupHead == null));
}
{
    const sky = [note('a', ['wide', 'narrow']), note('b', ['wide', 'narrow']), note('c', ['wide', 'narrow']),
        note('d', ['wide']), note('e', ['wide']), note('f', ['wide'])];
    groupNotes(sky);
    check('the rarest shared tag wins', sky[0].groupLabel === '#narrow', sky[0].groupLabel);
    check('…and the rest still gather under the wider one', sky[3].groupLabel === '#wide', sky[3].groupLabel);
}
{
    const sky = [note('a', ['t']), note('b', ['t']), note('c', ['t'], { category: 'Science' }), note('d', ['t'], { category: 'Science' })];
    groupNotes(sky);
    check('a group never crosses a galaxy', sky.every(n => n.groupHead == null));
}
{
    const sky = [note('a', ['t']), note('b', ['t'], { brightness: 0.9 }), note('c', ['t'])];
    groupNotes(sky);
    check('the strongest note is the head', sky[0].groupHead === 1 && sky[0].groupCount === 3, sky[0]);
}
{
    const sky = [note('a', ['t']), note('b', ['t']), note('c', ['t'])].map(n => ({ ...n, sourceKind: 'code' }));
    groupNotes(sky);
    check('notes rules leave code alone', sky.every(n => n.groupHead == null));
}
{
    const sky = [note('a', ['t'], { brightness: 0.9, local: { u: 10, v: 0, n: 2 } }),
        note('b', ['t'], { local: { u: -40, v: 30, n: 12 } }), note('c', ['t'], { local: { u: 50, v: -9, n: -7 } })];
    groupNotes(sky);
    gatherGroups(sky, n => { n.position.x = n.local.u; n.position.y = n.local.v; n.position.z = n.local.n; });
    sky[0].position = { x: 10, y: 0, z: 2 };
    check('the head stays where the layout put it', sky[0].local.u === 10 && sky[0].local.v === 0);
    check('members come to its side', sky.slice(1).every(n => dist(n.position, sky[0].position) < 8), sky.map(n => n.position));
    check('the group knows how far it reaches', sky[1].groupR > 0 && sky[1].groupR === sky[0].groupR);
}

// ── the same sky, built for real ──
const CATS = ['Programming', 'Science', 'Design_Art', 'Engineering', 'Education', 'Lifestyle'];
const rawNotes = Array.from({ length: 120 }, (_, i) => ({
    Id: `note${i}`, Title: `Note ${i}`, PrimaryCategory: CATS[i % CATS.length], WordCount: 200 + i,
    Importance: 1 + (i % 7), Tags: [`topic${i % 5}`], LinkedNodeIds: [],
}));
const symbols = [];
for (let f = 0; f < 12; f++) {
    const file = `src/File${f}.cs`;
    symbols.push({ id: `file${f}`, title: file, kind: 'file', file, repo: 'brainx', source: 'brainx' });
    for (let s = 0; s < 4; s++) symbols.push({ id: `f${f}s${s}`, title: `F${f}.S${s}`, kind: 'method', file, repo: 'brainx', source: 'brainx' });
}
const hubLinks = Array.from({ length: 30 }, (_, i) => ({ noteId: `note${i}`, symbolId: 'f7s0' }));
const overlay = { nodes: symbols, edges: [{ from: 'f7s0', to: 'f3s1', relation: 'calls' }], mentions: hubLinks };
const payload = { Nodes: rawNotes, CodeOverlay: overlay };

{
    const u = buildUniverse(payload);
    const g = u.galaxies.find(x => x.isCode);
    const code = u.nodes.filter(n => n.sourceKind === 'code');
    const notes = u.galaxies.filter(x => !x.isCode);
    check('code sits at the centre of the universe', len(g.center) < 1, g.center);
    check('…inside every note galaxy', Math.min(...notes.map(x => len(x.center))) > Math.max(...code.map(n => len(n.position))),
        [Math.min(...notes.map(x => len(x.center))), Math.max(...code.map(n => len(n.position)))]);
    const byFile = f => code.filter(n => n.codeFile === f);
    const hub = byFile('src/File7.cs').find(n => n.codeKind === 'file');
    const rim = Math.max(...code.filter(n => n.codeKind === 'file').map(n => len(n.position)));
    check('the most connected file is nearest the middle',
        len(hub.position) === Math.min(...code.filter(n => n.codeKind === 'file').map(n => len(n.position))) && len(hub.position) < rim);
    check('…and the brightest', hub.brightness === Math.max(...code.map(n => n.brightness)));
    check('a file is the head of its symbols', byFile('src/File7.cs').every(n => u.nodes[n.groupHead] === hub), hub.title);
    check('…counted with them', hub.groupCount === 5, hub.groupCount);
    check('…labelled by its name', hub.groupLabel === 'File7.cs', hub.groupLabel);
    check('a symbol stays near its file', byFile('src/File7.cs').every(n => dist(n.position, hub.position) <= hub.groupR + 1e-9) && hub.groupR > 0);
    check('mentions still link note and symbol', u.edges.filter(e => e.kind === 'mention').length === 30);
    check('code edges still link', u.edges.filter(e => e.kind === 'code').length === 1);
    check('a note group gathers in its galaxy', u.nodes.some(n => n.sourceKind !== 'code' && n.groupHead != null));
    const notePoints = new Set(u.nodes.filter(n => n.sourceKind !== 'code').map(n => n.id));
    check('note ids survive', rawNotes.every(n => notePoints.has(n.Id)));
    const head = u.nodes.find((n, i) => n.groupHead === i && n.sourceKind !== 'code');
    const members = u.nodes.filter(n => n.groupHead != null && u.nodes[n.groupHead] === head);
    check('members meet the head in world space', members.every(n => dist(n.position, head.position) <= head.groupR + 1e-9));
}
{
    const twoRepos = symbols.concat([
        { id: 'o1', title: 'other.py', kind: 'file', file: 'other.py', repo: 'second', source: 'graphify' },
        { id: 'o2', title: 'Other.run', kind: 'method', file: 'other.py', repo: 'second', source: 'graphify' },
    ]);
    const u = buildUniverse({ Nodes: rawNotes, CodeOverlay: { nodes: twoRepos } });
    const [a, b] = u.galaxies.filter(g => g.isCode);
    check('two repos are two clusters in the core', a && b && dist(a.center, b.center) >= Math.min(a.radius, b.radius), [a?.center, b?.center]);
    const rim = Math.min(...u.galaxies.filter(g => !g.isCode).map(g => len(g.center)));
    check('…still inside the galaxies', len(a.center) + a.radius < rim && len(b.center) + b.radius < rim);
    check('…and the source is told apart', b.label.includes('Graphify'), b.label);
}
{
    const one = rawNotes.map(n => ({ ...n, PrimaryCategory: 'Programming' }));
    const u = buildUniverse({ Nodes: one, CodeOverlay: overlay });
    const g = u.galaxies.find(x => !x.isCode), c = u.galaxies.find(x => x.isCode);
    check('a one-category vault keeps its galaxy at the origin', len(g.center) === 0);
    check('…and the core goes beside it, not through it', dist(c.center, g.center) > c.radius + g.radius, [c.center, g.radius, c.radius]);
}
{
    const u = buildUniverse({ Nodes: [], CodeOverlay: overlay });
    check('an empty vault still draws its code', u.nodes.length === symbols.length && u.galaxies.length === 1);
    check('…without a mention to a note that is not there', u.edges.every(e => e.kind === 'code'));
}
check('no code leaves the sky as it was', !buildUniverse({ Nodes: rawNotes }).galaxies.some(g => g.isCode));

{
    const b = buildBrainGraph(payload);
    const g = b.galaxies.find(x => x.isCode);
    const code = b.nodes.filter(n => n.sourceKind === 'code');
    check('the brain theme draws code', code.length === symbols.length, code.length);
    check('…in the thalamus', g.regionKey === 'THALAMUS' && g.regionLabel.startsWith('Thalamus'), g.regionLabel);
    check('…deep inside the head', code.every(n => Number.isFinite(n.position.x) && dist(n.position, g.center) < 40), code[0].position);
    check('…a volume the scene can fly to', g.kind === 'volume' && g.normal && g.su > 0);
    check('symbols group by file here too', code.find(n => n.codeKind === 'file').groupCount === 5);
    const mention = b.edges.find(e => e.kind === 'mention');
    const route = [0, 1, 2, 3, 4].map(() => ({ x: 0, y: 0, z: 0 }));
    const points = fiberRoute(b.nodes[mention.a], b.nodes[mention.b], route);
    check('a mention routes as a fibre', points >= 4 && route.slice(0, points).every(p => Number.isFinite(p.x + p.y + p.z)), route);

    const manyCats = Array.from({ length: 40 }, (_, i) => ({ Id: `c${i}`, Title: `C${i}`, PrimaryCategory: `Unknown${i}`, WordCount: 10 }));
    const packed = buildBrainGraph({ Nodes: manyCats, CodeOverlay: overlay });
    check('no unknown category takes the thalamus', packed.galaxies.filter(x => x.regionKey === 'THALAMUS').every(x => x.isCode));
    check('an empty vault still has a thalamus', buildBrainGraph({ Nodes: [], CodeOverlay: overlay }).nodes.length === symbols.length);
    check('no notes and no code stays empty', buildBrainGraph({ Nodes: [] }).nodes.length === 0);
}

console.log(`${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
