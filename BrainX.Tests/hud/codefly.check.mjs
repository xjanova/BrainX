// What the code dashboard tells the owner after a flight. Runs without a browser:
//     node BrainX.Tests/hud/codefly.check.mjs
// Exit 0 = every check passed. Same contract as the C# harness.

import { flyToCode } from '../../BrainX.Client/wwwroot/universe/codefly.js';

let passed = 0, failed = 0;
function check(name, ok, detail) {
    if (ok) { passed++; return; }
    failed++;
    console.log(`FAIL ${name}${detail !== undefined ? `\n     ${JSON.stringify(detail)}` : ''}`);
}

// A scene as app.js sees it: what it holds can change with no mount from app.js,
// which is what setTheme does.
function sky(theme, nodes, galaxies) {
    const scene = {
        theme, nodes, galaxies, flown: [],
        getTheme: () => scene.theme,
        focusNodeId: id => { if (!scene.nodes.includes(id)) return false; scene.flown.push(id); return true; },
        focusGalaxy: category => { if (!scene.galaxies.includes(category)) return false; scene.flown.push(category); return true; },
    };
    return scene;
}

const code = 'code:brainx';

check('no scene yet', flyToCode(null, { id: 's', category: code }) === 'none');

const drawn = sky('universe', ['s'], [code]);
check('a drawn symbol is flown to', flyToCode(drawn, { id: 's', category: code }) === 'node' && drawn.flown[0] === 's', drawn.flown);

const fallback = sky('universe', [], [code]);
check('an undrawn symbol falls back to its galaxy', flyToCode(fallback, { id: 's', category: code }) === 'galaxy' && fallback.flown[0] === code);
check('the repo tile has no id', flyToCode(sky('universe', [], [code]), { category: code }) === 'galaxy');
check('a galaxy the sky lacks is none', flyToCode(sky('universe', [], []), { id: 's', category: code }) === 'none');
check('a note outside the view is none', flyToCode(sky('universe', [], [code]), { id: 'note' }) === 'none');

const brain = sky('brain', ['note', 's'], ['Programming', code]);
check('a symbol flies in the brain theme too', flyToCode(brain, { id: 's', category: code }) === 'node' && brain.flown[0] === 's', brain.flown);
check('a note still flies in the brain theme', flyToCode(brain, { id: 'note' }) === 'node');
check('the repo tile reaches its thalamus', flyToCode(sky('brain', [], [code]), { category: code }) === 'galaxy');

// The bug: a graph built under the brain theme joined the sky at the switch, and
// the page, reading its own earlier copy, said the repo was not drawn.
const switched = sky('brain', [], ['Programming']);
check('before the switch', flyToCode(switched, { category: code }) === 'none');
switched.theme = 'universe';
switched.galaxies = ['Programming', code];
check('after the switch the new galaxy is reached', flyToCode(switched, { category: code }) === 'galaxy', switched.flown);

console.log(`${passed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
