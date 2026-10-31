import assert from 'node:assert/strict';
import { buildUniverse } from './layout.js';

const brain = {
    Nodes: [{ Id: 'note-1', Title: 'Why', PrimaryCategory: 'Programming', WordCount: 50,
        LinkedNodeIds: ['note-2'], AutoLinkedNodeIds: [] },
        { Id: 'note-2', Title: 'How', PrimaryCategory: 'Programming', WordCount: 40,
            LinkedNodeIds: [], AutoLinkedNodeIds: [] }],
    CodeOverlay: {
        nodes: [
            { id: 'repo:A.cs', title: 'A.cs', kind: 'file', repo: 'repo', source: 'graphify', file: 'A.cs' },
            { id: 'repo:A.cs#A.Run', title: 'A.Run', kind: 'method', repo: 'repo', source: 'graphify', file: 'A.cs', line: 3 }
        ],
        edges: [{ from: 'repo:A.cs', to: 'repo:A.cs#A.Run', relation: 'contains' }],
        mentions: [{ noteId: 'note-1', symbolId: 'repo:A.cs#A.Run' },
            { noteId: 'gone', symbolId: 'repo:A.cs#A.Run' }]
    }
};
const plain = buildUniverse({ Nodes: brain.Nodes });
assert.equal(plain.nodes.length, 2);
assert.equal(plain.edges.length, 1);

const layered = buildUniverse(brain);
assert.equal(layered.nodes.length, 4);
assert.equal(layered.nodes[0].id, 'note-1');
assert.equal(layered.nodes[0].sourceKind, 'note');
assert.equal(layered.nodes[1].id, 'note-2');
assert.equal(layered.nodes[3].sourceKind, 'code');
assert.equal(layered.nodes[3].codeSource, 'Graphify');
assert.equal(layered.galaxies.length, 2);
assert.equal(layered.galaxies[1].isCode, true);
assert.equal(layered.edges.filter(e => e.kind === 'mention').length, 1);
assert.equal(layered.edges.filter(e => e.kind === 'code').length, 1);
assert.equal(layered.edges.filter(e => !e.kind).length, 1);
assert(layered.nodes[3].position.x > layered.nodes[0].position.x);
const noteExtent = Math.max(...layered.galaxies.filter(g => !g.isCode)
    .map(g => Math.hypot(g.center.x, g.center.y, g.center.z) + g.radius));
const codeGalaxy = layered.galaxies.find(g => g.isCode);
assert(codeGalaxy.center.x - codeGalaxy.radius > noteExtent);

const codeOnly = buildUniverse({ Nodes: [], CodeOverlay: brain.CodeOverlay });
assert.equal(codeOnly.nodes.length, 2);
assert.equal(codeOnly.edges.filter(e => e.kind === 'code').length, 1);
